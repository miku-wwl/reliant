using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Dto;
using Reliant.Application.Messaging;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using MediatR;
using System.Text.Json;

namespace Reliant.Worker.Handlers;

public class ProcessingHandlerService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ProcessingHandlerService> logger) : BackgroundService
{
    private readonly string QueueName = configuration["Queue:QueueName"] ?? "reliant-processing";
    private const int Concurrency = 10;
    private readonly int VisibilityTimeoutSeconds = configuration.GetValue<int?>("Worker:VisibilityTimeoutSeconds") ?? 35;
    private const int LeaseSeconds = 30;
    private const int HeartbeatIntervalMs = 10000;
    private static readonly RetryPolicy RetryPolicy = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Processing Handler started");

        using var semaphore = new SemaphoreSlim(Concurrency, Concurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            await semaphore.WaitAsync(stoppingToken);

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var queueAdapter = scope.ServiceProvider.GetRequiredService<IQueueAdapter>();

                    var queueUrl = await queueAdapter.GetOrCreateQueueAsync(QueueName, stoppingToken);
                    var message = await queueAdapter.ReceiveAsync(queueUrl, VisibilityTimeoutSeconds, stoppingToken);

                    if (message is null) return;

                    logger.LogInformation("Processing message {MessageId}", message.MessageId);

                    using var innerScope = serviceProvider.CreateScope();
                    var inboxRepo = innerScope.ServiceProvider.GetRequiredService<IInboxRepository>();
                    var contributionRepo = innerScope.ServiceProvider.GetRequiredService<IContributionRepository>();
                    var stateTransitionRepo = innerScope.ServiceProvider.GetRequiredService<IStateTransitionRepository>();
                    var leaseRepo = innerScope.ServiceProvider.GetRequiredService<ILeaseRepository>();
                    var deadLetterRepo = innerScope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
                    var innerUnitOfWork = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var dbContext = innerScope.ServiceProvider.GetRequiredService<ReliantDbContext>();
                    var faultInjector = innerScope.ServiceProvider.GetRequiredService<IWorkerFaultInjector>();
                    var sender = innerScope.ServiceProvider.GetRequiredService<ISender>();

                    var existing = await inboxRepo.GetByMessageIdAsync(message.MessageId, stoppingToken);
                    if (existing is { Status: InboxStatus.Processed })
                    {
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                        logger.LogInformation("Message {MessageId} already processed (inbox dedup)", message.MessageId);
                        return;
                    }

                    var workerId = Environment.MachineName + "-" + Guid.NewGuid();
                    var lease = new Lease
                    {
                        Id = Guid.NewGuid(),
                        WorkerId = workerId,
                        ExpiresAt = DateTime.UtcNow.AddSeconds(LeaseSeconds),
                        JobRunId = Guid.NewGuid()
                    };
                    await leaseRepo.AddAsync(lease, stoppingToken);

                    var heartbeatCts = new CancellationTokenSource();
                    var heartbeatTask = HeartbeatLoop(leaseRepo, lease.Id, heartbeatCts.Token);

                    try
                    {
                        var msg = JsonSerializer.Deserialize<ContributionProcessingMessage>(
                            message.Payload,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                            ?? throw new InvalidOperationException("Invalid processing message contract");
                        var contributionId = msg.ContributionId;
                        var organizationId = msg.OrganizationId;

                        TenantFilterAccessor.SetOrganizationId(organizationId);

                        var contribution = await contributionRepo.GetByIdAsync(contributionId, stoppingToken);
                        if (contribution is null)
                            throw new InvalidOperationException($"Contribution {contributionId} not found");

                        // Business facts always come from the database, never from the
                        // message payload, so retry messages cannot drift from reality.
                        var amount = contribution.Amount;
                        var currency = contribution.Currency;

                        // The recovery/entry path depends on the current persisted state:
                        //  - Created:       initial message -> Accepted -> Processing
                        //  - RetryPending:  retry message  -> Processing
                        //  - Processing:    redelivery/recovery -> resume, no re-entry
                        //  - terminal/other: idempotent ACK, never blindly submit
                        var skipProcessing = false;
                        switch (contribution.State)
                        {
                            case ContributionState.Created:
                                // Every actual state change gets its own audit row:
                                // Created -> Accepted, then Accepted -> Processing.
                                var fromCreated = contribution.State;
                                contribution.TransitionTo(ContributionState.Accepted, "Worker accepted");
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = fromCreated,
                                    ToState = ContributionState.Accepted,
                                    Reason = "Worker accepted",
                                    ChangedBy = workerId
                                }, stoppingToken);

                                var fromAccepted = contribution.State;
                                contribution.TransitionTo(ContributionState.Processing, "Worker started processing");
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = fromAccepted,
                                    ToState = ContributionState.Processing,
                                    Reason = "Worker started processing",
                                    ChangedBy = workerId
                                }, stoppingToken);

                                await contributionRepo.UpdateAsync(contribution, stoppingToken);
                                await innerUnitOfWork.SaveChangesAsync(stoppingToken);
                                break;

                            case ContributionState.RetryPending:
                                contribution.TransitionTo(ContributionState.Processing, "Retry resumed by worker");
                                await contributionRepo.UpdateAsync(contribution, stoppingToken);
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = ContributionState.RetryPending,
                                    ToState = ContributionState.Processing,
                                    Reason = "Retry picked up by worker",
                                    ChangedBy = workerId
                                }, stoppingToken);
                                await innerUnitOfWork.SaveChangesAsync(stoppingToken);
                                break;

                            case ContributionState.Processing:
                                // Redelivery / recovery: already claimed, resume below.
                                logger.LogInformation("Contribution {ContributionId} redelivered while Processing, resuming", contributionId);
                                break;

                            case ContributionState.Succeeded:
                            case ContributionState.Failed:
                            case ContributionState.Completed:
                            default:
                                skipProcessing = true;
                                break;
                        }

                        if (skipProcessing)
                        {
                            logger.LogInformation("Contribution {ContributionId} in state {State}, idempotent ACK without submit", contributionId, contribution.State);
                            await inboxRepo.AddAsync(new InboxMessage
                            {
                                Id = Guid.NewGuid(),
                                MessageId = message.MessageId,
                                OrganizationId = organizationId,
                                MessageType = message.MessageType,
                                HandlerName = "ProcessingHandler",
                                HandlerVersion = "1.0",
                                ProcessedAt = DateTime.UtcNow,
                                Status = InboxStatus.Processed
                            }, stoppingToken);
                            await innerUnitOfWork.SaveChangesAsync(stoppingToken);
                            await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                            return;
                        }

                        var stateBeforeProviderCall = contribution.State;

                        var submitResult = await sender.Send(new SubmitToProviderCommand(
                            contributionId, organizationId, amount, currency, contribution.ExternalReference), stoppingToken);

                        // TRUE reload: a callback may have arrived and committed a state
                        // change while the provider call was in flight. The pre-call
                        // tracking entity is stale - detach it and reload from the
                        // database before making any post-call state decision, so a
                        // callback-applied terminal state is never overwritten.
                        dbContext.ChangeTracker.Clear();
                        var current = await contributionRepo.GetByIdAsync(contributionId, stoppingToken);
                        if (current is null)
                        {
                            throw new InvalidOperationException("Contribution disappeared during processing");
                        }

                        if (current.State != stateBeforeProviderCall)
                        {
                            logger.LogWarning("Contribution {ContributionId} state changed during provider call from {Before} to {After}, callback may have arrived first",
                                contributionId, stateBeforeProviderCall, current.State);
                        }

                        contribution = current;

                        if (submitResult.Disposition == ProviderSubmissionDisposition.DeferredBecauseCircuitOpen)
                        {
                            // Circuit open: no provider call happened, no business
                            // attempt, no retry budget consumed, no processed inbox
                            // and no SQS delete. Leave the message unacknowledged so
                            // it is redelivered after the circuit recovers.
                            logger.LogWarning("Contribution {ContributionId} deferred because circuit is open", contributionId);
                            return;
                        }

                        if (contribution.State == ContributionState.Succeeded)
                        {
                            logger.LogInformation("Contribution {ContributionId} already Succeeded (likely via callback), skipping state transition", contributionId);
                        }
                        else if (contribution.State is ContributionState.Failed or ContributionState.Completed)
                        {
                            logger.LogInformation("Contribution {ContributionId} already in terminal state {State}, skipping state transition", contributionId, contribution.State);
                        }
                        else if (submitResult.Status == AttemptStatus.Succeeded)
                        {
                            var fromState = contribution.State;
                            contribution.TransitionTo(ContributionState.Succeeded, "Provider succeeded");
                            await stateTransitionRepo.AddAsync(new StateTransition
                            {
                                Id = Guid.NewGuid(),
                                ContributionId = contributionId,
                                FromState = fromState,
                                ToState = ContributionState.Succeeded,
                                Reason = "Provider confirmed success",
                                ChangedBy = workerId
                            }, stoppingToken);
                        }
                        else if (submitResult.Status == AttemptStatus.Unknown)
                        {
                            // The actual machine executed Processing -> ProviderUnknown
                            // -> ReconciliationPending. Both transitions must be
                            // recorded; each FromState is captured before TransitionTo.
                            var fromProcessing = contribution.State;
                            contribution.TransitionTo(ContributionState.ProviderUnknown, "Provider timeout");
                            await stateTransitionRepo.AddAsync(new StateTransition
                            {
                                Id = Guid.NewGuid(),
                                ContributionId = contributionId,
                                FromState = fromProcessing,
                                ToState = ContributionState.ProviderUnknown,
                                Reason = $"Unknown outcome: {submitResult.ErrorMessage}",
                                ChangedBy = workerId
                            }, stoppingToken);

                            var fromUnknown = contribution.State;
                            contribution.TransitionTo(ContributionState.ReconciliationPending, "Awaiting reconciliation");
                            await stateTransitionRepo.AddAsync(new StateTransition
                            {
                                Id = Guid.NewGuid(),
                                ContributionId = contributionId,
                                FromState = fromUnknown,
                                ToState = ContributionState.ReconciliationPending,
                                Reason = "Unknown outcome: awaiting reconciliation",
                                ChangedBy = workerId
                            }, stoppingToken);
                        }
                        else
                        {
                            if (RetryPolicy.ShouldRetry(contribution.RetryCount + 1, submitResult.ErrorCategory))
                            {
                                var fromState = contribution.State;
                                contribution.RetryCount++;
                                contribution.LastErrorCategory = submitResult.ErrorCategory;
                                contribution.LastErrorMessage = submitResult.ErrorMessage;
                                contribution.NextRetryAt = DateTime.UtcNow.Add(RetryPolicy.GetDelay(contribution.RetryCount));
                                contribution.TransitionTo(ContributionState.RetryPending, "Retryable failure");
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = fromState,
                                    ToState = ContributionState.RetryPending,
                                    Reason = $"Retryable: {submitResult.ErrorMessage}",
                                    ChangedBy = workerId
                                }, stoppingToken);
                            }
                            else
                            {
                                var fromState = contribution.State;
                                contribution.TransitionTo(ContributionState.Failed, "Permanent failure");
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = fromState,
                                    ToState = ContributionState.Failed,
                                    Reason = $"Permanent: {submitResult.ErrorMessage}",
                                    ChangedBy = workerId
                                }, stoppingToken);
                            }
                        }

                        await contributionRepo.UpdateAsync(contribution, stoppingToken);
                        faultInjector.Inject(WorkerFaultPoint.AfterStateUpdated, contributionId.ToString());

                        var inboxMessage = new InboxMessage
                        {
                            Id = Guid.NewGuid(),
                            MessageId = message.MessageId,
                            OrganizationId = organizationId,
                            MessageType = message.MessageType,
                            HandlerName = "ProcessingHandler",
                            HandlerVersion = "1.0",
                            ProcessedAt = DateTime.UtcNow,
                            Status = InboxStatus.Processed
                        };
                        faultInjector.Inject(WorkerFaultPoint.BeforeInboxCommitted, contributionId.ToString());
                        await inboxRepo.AddAsync(inboxMessage, stoppingToken);

                        await innerUnitOfWork.SaveChangesAsync(stoppingToken);
                        faultInjector.Inject(WorkerFaultPoint.AfterInboxCommitted, contributionId.ToString());

                        faultInjector.Inject(WorkerFaultPoint.BeforeMessageAck, contributionId.ToString());
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);

                        logger.LogInformation("Message {MessageId} processed, attempt status: {Status}", message.MessageId, submitResult.Status);
                    }
                    catch (InvalidStateTransitionException ex)
                    {
                        logger.LogWarning(ex, "Invalid state transition for message {MessageId}", message.MessageId);
                        await deadLetterRepo.AddAsync(new DeadLetterRecord
                        {
                            Id = Guid.NewGuid(),
                            OrganizationId = Guid.Empty,
                            OriginalMessageId = message.MessageId,
                            MessageType = message.MessageType,
                            Payload = message.Payload,
                            ErrorCategory = ErrorCategory.PermanentBusinessRejection,
                            ErrorMessage = ex.Message,
                            AttemptCount = 1,
                            Status = DeadLetterStatus.Pending
                        }, stoppingToken);
                        await innerUnitOfWork.SaveChangesAsync(stoppingToken);
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing message {MessageId}", message.MessageId);
                    }
                    finally
                    {
                        heartbeatCts.Cancel();
                        try { await heartbeatTask; } catch { }
                        await leaseRepo.ReleaseAsync(lease.Id, stoppingToken);
                        TenantFilterAccessor.Clear();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Processing handler task error");
                }
                finally
                {
                    semaphore.Release();
                }
            }, stoppingToken);

            try { await Task.Delay(100, stoppingToken); } catch (OperationCanceledException) { }
        }

        logger.LogInformation("Processing Handler stopped");
    }

    private async Task HeartbeatLoop(ILeaseRepository leaseRepo, Guid leaseId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatIntervalMs, cancellationToken);
                await leaseRepo.RenewAsync(leaseId, DateTime.UtcNow.AddSeconds(LeaseSeconds), cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }
}
