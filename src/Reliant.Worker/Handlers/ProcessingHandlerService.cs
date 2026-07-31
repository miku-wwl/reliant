using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Messaging;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
using MediatR;
using System.Text.Json;

namespace Reliant.Worker.Handlers;

public class ProcessingHandlerService(
    IServiceProvider serviceProvider,
    ILogger<ProcessingHandlerService> logger) : BackgroundService
{
    private const string QueueName = "reliant-processing";
    private const int Concurrency = 10;
    private const int VisibilityTimeoutSeconds = 35;
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
                        var payload = JsonDocument.Parse(message.Payload);
                        var contributionId = payload.RootElement.GetProperty("contributionId").GetGuid();
                        var organizationId = payload.RootElement.GetProperty("organizationId").GetGuid();
                        var amount = payload.RootElement.GetProperty("amount").GetDecimal();
                        var currency = payload.RootElement.GetProperty("currency").GetString() ?? "USD";

                        TenantFilterAccessor.SetOrganizationId(organizationId);

                        var contribution = await contributionRepo.GetByIdAsync(contributionId, stoppingToken);
                        if (contribution is null)
                            throw new InvalidOperationException($"Contribution {contributionId} not found");

                        contribution.TransitionTo(ContributionState.Accepted, "Worker accepted");
                        contribution.TransitionTo(ContributionState.Processing, "Worker started processing");
                        await contributionRepo.UpdateAsync(contribution, stoppingToken);
                        await stateTransitionRepo.AddAsync(new StateTransition
                        {
                            Id = Guid.NewGuid(),
                            ContributionId = contributionId,
                            FromState = ContributionState.Created,
                            ToState = ContributionState.Processing,
                            Reason = "Processing started",
                            ChangedBy = workerId
                        }, stoppingToken);
                        await innerUnitOfWork.SaveChangesAsync(stoppingToken);

                        var submitResult = await sender.Send(new SubmitToProviderCommand(
                            contributionId, organizationId, amount, currency, contribution.ExternalReference), stoppingToken);

                        if (submitResult.Status == AttemptStatus.Succeeded)
                        {
                            contribution.TransitionTo(ContributionState.Succeeded, "Provider succeeded");
                            await stateTransitionRepo.AddAsync(new StateTransition
                            {
                                Id = Guid.NewGuid(),
                                ContributionId = contributionId,
                                FromState = ContributionState.Processing,
                                ToState = ContributionState.Succeeded,
                                Reason = "Provider confirmed success",
                                ChangedBy = workerId
                            }, stoppingToken);
                        }
                        else if (submitResult.Status == AttemptStatus.Unknown)
                        {
                            contribution.TransitionTo(ContributionState.ProviderUnknown, "Provider timeout");
                            contribution.TransitionTo(ContributionState.ReconciliationPending, "Awaiting reconciliation");
                            await stateTransitionRepo.AddAsync(new StateTransition
                            {
                                Id = Guid.NewGuid(),
                                ContributionId = contributionId,
                                FromState = ContributionState.Processing,
                                ToState = ContributionState.ReconciliationPending,
                                Reason = $"Unknown outcome: {submitResult.ErrorMessage}",
                                ChangedBy = workerId
                            }, stoppingToken);
                        }
                        else
                        {
                            if (RetryPolicy.ShouldRetry(1, submitResult.ErrorCategory))
                            {
                                contribution.TransitionTo(ContributionState.RetryPending, "Retryable failure");
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = ContributionState.Processing,
                                    ToState = ContributionState.RetryPending,
                                    Reason = $"Retryable: {submitResult.ErrorMessage}",
                                    ChangedBy = workerId
                                }, stoppingToken);
                            }
                            else
                            {
                                contribution.TransitionTo(ContributionState.Failed, "Permanent failure");
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = ContributionState.Processing,
                                    ToState = ContributionState.Failed,
                                    Reason = $"Permanent: {submitResult.ErrorMessage}",
                                    ChangedBy = workerId
                                }, stoppingToken);
                            }
                        }

                        await contributionRepo.UpdateAsync(contribution, stoppingToken);

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
                        await inboxRepo.AddAsync(inboxMessage, stoppingToken);

                        await innerUnitOfWork.SaveChangesAsync(stoppingToken);
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
