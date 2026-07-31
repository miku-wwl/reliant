using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Application.Messaging;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;
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
                    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                    var queueUrl = await queueAdapter.GetOrCreateQueueAsync(QueueName, stoppingToken);
                    var message = await queueAdapter.ReceiveAsync(queueUrl, VisibilityTimeoutSeconds, stoppingToken);

                    if (message is null)
                    {
                        return;
                    }

                    logger.LogInformation("Processing message {MessageId}", message.MessageId);

                    using var innerScope = serviceProvider.CreateScope();
                    var inboxRepo = innerScope.ServiceProvider.GetRequiredService<IInboxRepository>();
                    var contributionRepo = innerScope.ServiceProvider.GetRequiredService<IContributionRepository>();
                    var stateTransitionRepo = innerScope.ServiceProvider.GetRequiredService<IStateTransitionRepository>();
                    var leaseRepo = innerScope.ServiceProvider.GetRequiredService<ILeaseRepository>();
                    var deadLetterRepo = innerScope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
                    var innerUnitOfWork = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

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

                        TenantFilterAccessor.SetOrganizationId(organizationId);

                        var contribution = await contributionRepo.GetByIdAsync(contributionId, stoppingToken);
                        if (contribution is null)
                        {
                            throw new InvalidOperationException($"Contribution {contributionId} not found");
                        }

                        contribution.TransitionTo(ContributionState.Accepted, "Worker accepted");
                        contribution.TransitionTo(ContributionState.Processing, "Worker started processing");
                        await contributionRepo.UpdateAsync(contribution, stoppingToken);

                        var stateTransition = new StateTransition
                        {
                            Id = Guid.NewGuid(),
                            ContributionId = contributionId,
                            FromState = ContributionState.Created,
                            ToState = ContributionState.Processing,
                            Reason = "Processing started by worker",
                            ChangedBy = workerId
                        };
                        await stateTransitionRepo.AddAsync(stateTransition, stoppingToken);

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

                        logger.LogInformation("Message {MessageId} processed successfully", message.MessageId);
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
                        var retryable = RetryPolicy.ShouldRetry(1, ErrorCategory.ServerError);
                        if (!retryable)
                        {
                            await deadLetterRepo.AddAsync(new DeadLetterRecord
                            {
                                Id = Guid.NewGuid(),
                                OrganizationId = Guid.Empty,
                                OriginalMessageId = message.MessageId,
                                MessageType = message.MessageType,
                                Payload = message.Payload,
                                ErrorCategory = ErrorCategory.ServerError,
                                ErrorMessage = ex.Message,
                                AttemptCount = 1,
                                Status = DeadLetterStatus.Pending
                            }, stoppingToken);
                            await innerUnitOfWork.SaveChangesAsync(stoppingToken);
                            await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                        }
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

            try
            {
                await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException) { }
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
