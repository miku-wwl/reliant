using Microsoft.EntityFrameworkCore;
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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Reliant.Worker.Handlers;

public class ProcessingHandlerService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ProcessingHandlerService> logger) : BackgroundService
{
    private readonly string QueueName = configuration["Queue:QueueName"] ?? "reliant-processing";
    private readonly int ProcessingConcurrency = Math.Max(
        1,
        configuration.GetValue<int?>(
            "Worker:ProcessingConcurrency") ?? 10);
    private readonly int VisibilityTimeoutSeconds = configuration.GetValue<int?>("Worker:VisibilityTimeoutSeconds") ?? 35;
    private readonly int LeaseSeconds = Math.Max(
        1,
        configuration.GetValue<int?>("Worker:LeaseSeconds") ?? 30);
    private readonly int HeartbeatIntervalMs = Math.Max(
        100,
        configuration.GetValue<int?>("Worker:HeartbeatIntervalMs") ?? 10000);
    private readonly int MaxReceiveCount = Math.Max(
        1,
        configuration.GetValue<int?>("Queue:MaxReceiveCount") ?? 5);
    private static readonly RetryPolicy RetryPolicy = new();

    private sealed record MessageValidationResult(
        ContributionProcessingMessage? Message,
        Guid OrganizationId,
        string? ErrorMessage)
    {
        public bool IsValid =>
            Message is not null &&
            ErrorMessage is null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Processing Handler started");

        using var semaphore = new SemaphoreSlim(
            ProcessingConcurrency,
            ProcessingConcurrency);
        var inFlightTasks = new HashSet<Task>();
        var inFlightGate = new object();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await semaphore.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var processingTask = Task.Run(async () =>
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var queueAdapter = scope.ServiceProvider.GetRequiredService<IQueueAdapter>();

                    var queueUrl = await queueAdapter.GetOrCreateQueueAsync(QueueName, stoppingToken);
                    var message = await queueAdapter.ReceiveAsync(queueUrl, VisibilityTimeoutSeconds, stoppingToken);

                    if (message is null) return;

                    logger.LogInformation("Processing message {MessageId}", message.MessageId);
                    var jobRunId = ResolveJobRunId(message.MessageId);
                    var validation = ValidateProcessingMessage(message);
                    if (!validation.IsValid)
                    {
                        await HandlePoisonMessageAsync(
                            message,
                            validation.OrganizationId,
                            validation.ErrorMessage ??
                                "Invalid processing message contract",
                            stoppingToken);
                        return;
                    }

                    var msg = validation.Message!;
                    var contributionId = msg.ContributionId;
                    var organizationId = msg.OrganizationId;

                    using var innerScope = serviceProvider.CreateScope();
                    var inboxRepo = innerScope.ServiceProvider.GetRequiredService<IInboxRepository>();
                    var contributionRepo = innerScope.ServiceProvider.GetRequiredService<IContributionRepository>();
                    var stateTransitionRepo = innerScope.ServiceProvider.GetRequiredService<IStateTransitionRepository>();
                    var jobRunRepo = innerScope.ServiceProvider.GetRequiredService<IJobRunRepository>();
                    var jobAttemptRepo = innerScope.ServiceProvider.GetRequiredService<IJobAttemptRepository>();
                    var leaseRepo = innerScope.ServiceProvider.GetRequiredService<ILeaseRepository>();
                    var deadLetterRepo = innerScope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
                    var innerUnitOfWork = innerScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var dbContext = innerScope.ServiceProvider.GetRequiredService<ReliantDbContext>();
                    var faultInjector = innerScope.ServiceProvider.GetRequiredService<IWorkerFaultInjector>();
                    var sender = innerScope.ServiceProvider.GetRequiredService<ISender>();

                    // New work creates JobRun together with Outbox. This
                    // idempotent insert is the rolling-deployment fallback for
                    // messages that were already in SQS before that change.
                    await jobRunRepo.EnsurePendingAsync(new JobRun
                    {
                        Id = jobRunId,
                        OrganizationId = organizationId,
                        JobDefinitionId =
                            KnownJobDefinitions.ContributionProcessingId,
                        QueueUrl = QueueName,
                        MessageId = message.MessageId,
                        Payload = message.Payload,
                        Status = JobStatus.Pending,
                        CreatedAt = DateTime.UtcNow,
                        FencingToken = 0,
                        Version = 0
                    }, stoppingToken);

                    var existing = await inboxRepo.GetByMessageIdAsync(message.MessageId, stoppingToken);
                    if (existing is { Status: InboxStatus.Processed })
                    {
                        var completedAt = DateTime.UtcNow;
                        var completedJob = await jobRunRepo.GetByIdAsync(
                            jobRunId,
                            stoppingToken);
                        completedJob?.MarkSucceeded(completedAt);
                        var openAttempt =
                            await jobAttemptRepo.GetRunningByJobRunAsync(
                                jobRunId,
                                stoppingToken);
                        openAttempt?.Complete(
                            JobAttemptStatus.Succeeded,
                            completedAt);
                        await innerUnitOfWork.SaveChangesAsync(
                            stoppingToken);
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                        logger.LogInformation("Message {MessageId} already processed (inbox dedup)", message.MessageId);
                        return;
                    }

                    var workerId = Environment.MachineName + "-" + Guid.NewGuid();
                    var lease = new Lease
                    {
                        Id = Guid.NewGuid(),
                        JobRunId = jobRunId,
                        WorkerId = workerId,
                        ExpiresAt = DateTime.UtcNow.AddSeconds(LeaseSeconds)
                    };

                    JobAttempt jobAttempt;
                    await innerUnitOfWork.BeginTransactionAsync(
                        stoppingToken);
                    try
                    {
                        if (!await leaseRepo.TryAcquireAsync(
                            lease,
                            stoppingToken))
                        {
                            await innerUnitOfWork.RollbackAsync(
                                stoppingToken);

                            // A redelivery can become visible before the
                            // crashed owner's lease expires. Do not ACK or
                            // process it yet.
                            var activeLease =
                                await leaseRepo.GetActiveByJobRunAsync(
                                    jobRunId,
                                    stoppingToken);
                            logger.LogWarning(
                                "Message {MessageId} deferred because job {JobRunId} lease remains owned by {WorkerId} until {ExpiresAt}",
                                message.MessageId,
                                jobRunId,
                                activeLease?.WorkerId ?? "unknown",
                                activeLease?.ExpiresAt);
                            return;
                        }

                        var acquiredAt = DateTime.UtcNow;
                        var jobRun = await jobRunRepo.GetByIdAsync(
                            jobRunId,
                            stoppingToken)
                            ?? throw new InvalidOperationException(
                                $"JobRun {jobRunId} disappeared");

                        // Defensive recovery for an administrator-released
                        // Lease. The normal expiry scanner already closes the
                        // crashed owner's running attempt.
                        var abandonedAttempt =
                            await jobAttemptRepo.GetRunningByJobRunAsync(
                                jobRunId,
                                stoppingToken);
                        abandonedAttempt?.Complete(
                            JobAttemptStatus.Abandoned,
                            acquiredAt,
                            "A new owner acquired the job after the previous lease ended");

                        var attemptNumber =
                            jobRun.StartAttempt(acquiredAt);
                        jobAttempt = new JobAttempt
                        {
                            Id = Guid.NewGuid(),
                            JobRunId = jobRunId,
                            AttemptNumber = attemptNumber,
                            LeaseId = lease.Id,
                            FencingToken =
                                lease.FencingToken,
                            WorkerId = workerId,
                            StartedAt = acquiredAt,
                            Status = JobAttemptStatus.Running
                        };
                        await jobAttemptRepo.AddAsync(
                            jobAttempt,
                            stoppingToken);
                        await innerUnitOfWork.SaveChangesAsync(
                            stoppingToken);
                        await innerUnitOfWork.CommitAsync(
                            stoppingToken);
                    }
                    catch
                    {
                        await innerUnitOfWork.RollbackAsync(
                            CancellationToken.None);
                        throw;
                    }

                    logger.LogInformation(
                        "Worker {WorkerId} acquired lease {LeaseId} for job {JobRunId} with fencing token {FencingToken}; attempt {AttemptNumber} started",
                        workerId,
                        lease.Id,
                        jobRunId,
                        lease.FencingToken,
                        jobAttempt.AttemptNumber);

                    var executionFence =
                        new JobExecutionFence(
                            jobRunId,
                            lease.Id,
                            lease.FencingToken);
                    var heartbeatCts = new CancellationTokenSource();
                    var heartbeatTask = HeartbeatLoop(
                        lease.Id,
                        heartbeatCts.Token);

                    try
                    {
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
                                await SaveFencedAsync(
                                    innerUnitOfWork,
                                    leaseRepo,
                                    executionFence,
                                    stoppingToken);
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
                                await SaveFencedAsync(
                                    innerUnitOfWork,
                                    leaseRepo,
                                    executionFence,
                                    stoppingToken);
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
                            await ApplyJobOutcomeAsync(
                                jobRunRepo,
                                jobAttemptRepo,
                                jobRunId,
                                jobAttempt.Id,
                                JobAttemptStatus.Succeeded,
                                JobStatus.Succeeded,
                                null,
                                stoppingToken);
                            await SaveFencedAsync(
                                innerUnitOfWork,
                                leaseRepo,
                                executionFence,
                                stoppingToken);
                            await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                            return;
                        }

                        var stateBeforeProviderCall = contribution.State;

                        var submitResult = await sender.Send(
                            new SubmitToProviderCommand(
                                contributionId,
                                organizationId,
                                amount,
                                currency,
                                contribution.ExternalReference,
                                executionFence),
                            stoppingToken);
                        var jobAttemptOutcome =
                            JobAttemptStatus.Succeeded;
                        var jobOutcome = JobStatus.Succeeded;
                        string? jobError = null;

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
                            await ApplyJobOutcomeAsync(
                                jobRunRepo,
                                jobAttemptRepo,
                                jobRunId,
                                jobAttempt.Id,
                                JobAttemptStatus.Deferred,
                                JobStatus.Pending,
                                "Provider circuit is open",
                                stoppingToken);
                            await SaveFencedAsync(
                                innerUnitOfWork,
                                leaseRepo,
                                executionFence,
                                stoppingToken);
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
                            var currentAttempt =
                                contribution.RetryCount + 1;
                            if (submitResult.ErrorCategory is not null &&
                                ErrorClassifier.IsRetryable(
                                    submitResult.ErrorCategory.Value))
                            {
                                var fromState = contribution.State;
                                contribution.RetryCount =
                                    currentAttempt;
                                contribution.LastErrorCategory = submitResult.ErrorCategory;
                                contribution.LastErrorMessage = submitResult.ErrorMessage;
                                string transitionReason;

                                if (RetryPolicy.ShouldRetry(
                                    currentAttempt,
                                    submitResult.ErrorCategory))
                                {
                                    var retryDelay =
                                        RetryPolicy.GetDelay(
                                            currentAttempt);
                                    var delayMilliseconds = (long)Math.Round(
                                        retryDelay.TotalMilliseconds,
                                        MidpointRounding.AwayFromZero);
                                    contribution.NextRetryAt =
                                        DateTime.UtcNow.Add(
                                            retryDelay);
                                    transitionReason =
                                        "Retryable failure";
                                    logger.LogInformation(
                                        "Retry backoff scheduled for contribution {ContributionId}: attempt {Attempt}/{MaxAttempts}, delay {DelayMs} ms, next {NextRetryAt:O}",
                                        contributionId,
                                        currentAttempt,
                                        RetryPolicy.MaxAttempts,
                                        delayMilliseconds,
                                        contribution.NextRetryAt);
                                }
                                else
                                {
                                    // The final provider attempt is durable,
                                    // then the existing scheduler immediately
                                    // owns Failed + DeadLetter + alert as one
                                    // transaction. No sixth provider call is
                                    // dispatched.
                                    contribution.NextRetryAt =
                                        DateTime.UtcNow;
                                    transitionReason =
                                        "Retry budget exhausted; awaiting dead-letter finalization";
                                    jobAttemptOutcome =
                                        JobAttemptStatus.Failed;
                                    jobOutcome =
                                        JobStatus.DeadLettered;
                                    jobError =
                                        submitResult.ErrorMessage;
                                    logger.LogError(
                                        "Contribution {ContributionId} retry exhausted after {AttemptCount} attempts; dead-letter finalization queued",
                                        contributionId,
                                        currentAttempt);
                                }

                                contribution.TransitionTo(
                                    ContributionState.RetryPending,
                                    transitionReason);
                                await stateTransitionRepo.AddAsync(new StateTransition
                                {
                                    Id = Guid.NewGuid(),
                                    ContributionId = contributionId,
                                    FromState = fromState,
                                    ToState = ContributionState.RetryPending,
                                    Reason =
                                        $"{transitionReason}: {submitResult.ErrorMessage}",
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

                        await ApplyJobOutcomeAsync(
                            jobRunRepo,
                            jobAttemptRepo,
                            jobRunId,
                            jobAttempt.Id,
                            jobAttemptOutcome,
                            jobOutcome,
                            jobError,
                            stoppingToken);
                        await SaveFencedAsync(
                            innerUnitOfWork,
                            leaseRepo,
                            executionFence,
                            stoppingToken);
                        faultInjector.Inject(WorkerFaultPoint.AfterInboxCommitted, contributionId.ToString());

                        faultInjector.Inject(WorkerFaultPoint.BeforeMessageAck, contributionId.ToString());
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);

                        logger.LogInformation("Message {MessageId} processed, attempt status: {Status}", message.MessageId, submitResult.Status);
                    }
                    catch (StaleJobOwnerException ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Fencing rejected stale worker {WorkerId} for job {JobRunId}; lease {LeaseId}; token {FencingToken}; message left unacknowledged",
                            workerId,
                            ex.Fence.JobRunId,
                            ex.Fence.LeaseId,
                            ex.Fence.FencingToken);
                    }
                    catch (OperationCanceledException)
                        when (stoppingToken.IsCancellationRequested)
                    {
                        const string shutdownReason =
                            "Worker graceful shutdown interrupted processing";
                        logger.LogInformation(
                            "Graceful shutdown interrupted message {MessageId}; marking attempt Abandoned, returning job to Pending, releasing lease, and leaving message unacknowledged",
                            message.MessageId);
                        await RecordInterruptedJobAsync(
                            organizationId,
                            jobRunId,
                            jobAttempt.Id,
                            JobAttemptStatus.Abandoned,
                            shutdownReason,
                            markJobPending: true);
                    }
                    catch (DbUpdateConcurrencyException ex)
                    {
                        // Another concurrent delivery committed the same
                        // Contribution state first. Leave this physical message
                        // unacknowledged; after visibility expiry it will observe
                        // the winner's Inbox row and follow the normal dedup path.
                        logger.LogWarning(
                            ex,
                            "Message {MessageId} lost optimistic concurrency; leaving unacknowledged for Inbox recovery",
                            message.MessageId);
                        await RecordInterruptedJobAsync(
                            organizationId,
                            jobRunId,
                            jobAttempt.Id,
                            JobAttemptStatus.Abandoned,
                            ex.Message,
                            markJobPending: false);
                    }
                    catch (InvalidStateTransitionException ex)
                    {
                        logger.LogWarning(ex, "Invalid state transition for message {MessageId}", message.MessageId);
                        await deadLetterRepo.AddAsync(new DeadLetterRecord
                        {
                            Id = Guid.NewGuid(),
                            OrganizationId = organizationId,
                            OriginalMessageId = message.MessageId,
                            MessageType = message.MessageType,
                            Payload = message.Payload,
                            ErrorCategory = ErrorCategory.PermanentBusinessRejection,
                            ErrorMessage = ex.Message,
                            AttemptCount = 1,
                            Status = DeadLetterStatus.Pending
                        }, stoppingToken);
                        await ApplyJobOutcomeAsync(
                            jobRunRepo,
                            jobAttemptRepo,
                            jobRunId,
                            jobAttempt.Id,
                            JobAttemptStatus.Failed,
                            JobStatus.DeadLettered,
                            ex.Message,
                            stoppingToken);
                        await SaveFencedAsync(
                            innerUnitOfWork,
                            leaseRepo,
                            executionFence,
                            stoppingToken);
                        await queueAdapter.DeleteAsync(queueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing message {MessageId}", message.MessageId);
                        await RecordInterruptedJobAsync(
                            organizationId,
                            jobRunId,
                            jobAttempt.Id,
                            JobAttemptStatus.Failed,
                            ex.Message,
                            markJobPending: true);
                    }
                    finally
                    {
                        heartbeatCts.Cancel();
                        try { await heartbeatTask; } catch { }
                        await leaseRepo.ReleaseAsync(
                            lease.Id,
                            CancellationToken.None);
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
            }, CancellationToken.None);

            lock (inFlightGate)
            {
                inFlightTasks.Add(processingTask);
            }
            _ = processingTask.ContinueWith(
                completedTask =>
                {
                    lock (inFlightGate)
                    {
                        inFlightTasks.Remove(completedTask);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try { await Task.Delay(100, stoppingToken); } catch (OperationCanceledException) { }
        }

        Task[] tasksToDrain;
        lock (inFlightGate)
        {
            tasksToDrain = [.. inFlightTasks];
        }
        if (tasksToDrain.Length > 0)
        {
            logger.LogInformation(
                "Processing Handler draining {TaskCount} in-flight processing task(s)",
                tasksToDrain.Length);
            await Task.WhenAll(tasksToDrain);
        }

        logger.LogInformation("Processing Handler stopped");
    }

    private static async Task ApplyJobOutcomeAsync(
        IJobRunRepository jobRunRepo,
        IJobAttemptRepository jobAttemptRepo,
        Guid jobRunId,
        Guid jobAttemptId,
        JobAttemptStatus attemptStatus,
        JobStatus? jobStatus,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var completedAt = DateTime.UtcNow;
        var attempt = await jobAttemptRepo.GetByIdAsync(
            jobAttemptId,
            cancellationToken);
        attempt?.Complete(
            attemptStatus,
            completedAt,
            errorMessage);

        if (!jobStatus.HasValue)
        {
            return;
        }

        var jobRun = await jobRunRepo.GetByIdAsync(
            jobRunId,
            cancellationToken);
        if (jobRun is null)
        {
            return;
        }

        switch (jobStatus.Value)
        {
            case JobStatus.Pending:
                jobRun.MarkPending();
                break;
            case JobStatus.Succeeded:
                jobRun.MarkSucceeded(completedAt);
                break;
            case JobStatus.DeadLettered:
                jobRun.MarkDeadLettered(completedAt);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Job outcome {jobStatus.Value}");
        }
    }

    private static async Task SaveFencedAsync(
        IUnitOfWork unitOfWork,
        ILeaseRepository leaseRepository,
        JobExecutionFence fence,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginTransactionAsync(
            cancellationToken);
        try
        {
            if (!await leaseRepository
                .TryLockCurrentOwnerAsync(
                    fence,
                    DateTime.UtcNow,
                    cancellationToken))
            {
                throw new StaleJobOwnerException(fence);
            }

            await unitOfWork.SaveChangesAsync(
                cancellationToken);
            await unitOfWork.CommitAsync(
                cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackAsync(
                CancellationToken.None);
            throw;
        }
    }

    private async Task RecordInterruptedJobAsync(
        Guid organizationId,
        Guid jobRunId,
        Guid jobAttemptId,
        JobAttemptStatus attemptStatus,
        string? errorMessage,
        bool markJobPending)
    {
        TenantFilterAccessor.SetOrganizationId(organizationId);
        using var recoveryScope = serviceProvider.CreateScope();
        var jobRunRepo = recoveryScope.ServiceProvider
            .GetRequiredService<IJobRunRepository>();
        var jobAttemptRepo = recoveryScope.ServiceProvider
            .GetRequiredService<IJobAttemptRepository>();
        var unitOfWork = recoveryScope.ServiceProvider
            .GetRequiredService<IUnitOfWork>();

        await ApplyJobOutcomeAsync(
            jobRunRepo,
            jobAttemptRepo,
            jobRunId,
            jobAttemptId,
            attemptStatus,
            markJobPending ? JobStatus.Pending : null,
            errorMessage,
            CancellationToken.None);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    private static Guid ResolveJobRunId(string messageId)
    {
        if (Guid.TryParse(messageId, out var parsed))
        {
            return parsed;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static MessageValidationResult ValidateProcessingMessage(
        IQueueMessage message)
    {
        ContributionProcessingMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<
                ContributionProcessingMessage>(
                message.Payload,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
        }
        catch (JsonException ex)
        {
            return new MessageValidationResult(
                null,
                Guid.Empty,
                $"Payload is not valid JSON for the processing contract: {ex.Message}");
        }

        if (parsed is null)
        {
            return new MessageValidationResult(
                null,
                Guid.Empty,
                "Payload JSON resolved to a null processing contract");
        }

        if (message.MessageType is not
            ("ContributionCreated" or
            "ContributionRetryRequested"))
        {
            return new MessageValidationResult(
                parsed,
                parsed.OrganizationId,
                $"Unsupported processing message type {message.MessageType}");
        }

        if (parsed.Version != 1)
        {
            return new MessageValidationResult(
                parsed,
                parsed.OrganizationId,
                $"Unsupported processing message version {parsed.Version}; expected version 1");
        }

        if (parsed.ContributionId == Guid.Empty)
        {
            return new MessageValidationResult(
                parsed,
                parsed.OrganizationId,
                "Processing message ContributionId is required");
        }

        if (parsed.OrganizationId == Guid.Empty)
        {
            return new MessageValidationResult(
                parsed,
                Guid.Empty,
                "Processing message OrganizationId is required");
        }

        if (string.IsNullOrWhiteSpace(parsed.Trigger))
        {
            return new MessageValidationResult(
                parsed,
                parsed.OrganizationId,
                "Processing message Trigger is required");
        }

        if (string.IsNullOrWhiteSpace(parsed.CorrelationId))
        {
            return new MessageValidationResult(
                parsed,
                parsed.OrganizationId,
                "Processing message CorrelationId is required");
        }

        return new MessageValidationResult(
            parsed,
            parsed.OrganizationId,
            null);
    }

    private async Task HandlePoisonMessageAsync(
        IQueueMessage message,
        Guid organizationId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var receiveCount = Math.Max(
            1,
            message.ApproximateReceiveCount);
        logger.LogWarning(
            "Poison message {MessageId} rejected on receive {ReceiveCount}/{MaxReceiveCount}: {ErrorMessage}",
            message.MessageId,
            receiveCount,
            MaxReceiveCount,
            errorMessage);

        if (receiveCount < MaxReceiveCount)
        {
            return;
        }

        TenantFilterAccessor.SetOrganizationId(organizationId);
        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider
                .GetRequiredService<ReliantDbContext>();
            var deadLetterRepo = scope.ServiceProvider
                .GetRequiredService<IDeadLetterRepository>();
            var jobRunRepo = scope.ServiceProvider
                .GetRequiredService<IJobRunRepository>();
            var unitOfWork = scope.ServiceProvider
                .GetRequiredService<IUnitOfWork>();

            var existing = await dbContext.DeadLetterRecords
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    x =>
                        x.OriginalMessageId == message.MessageId &&
                        x.MessageType == message.MessageType,
                    cancellationToken);

            if (existing is null)
            {
                await deadLetterRepo.AddAsync(
                    new DeadLetterRecord
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = organizationId,
                        OriginalMessageId = message.MessageId,
                        MessageType = message.MessageType,
                        Payload = message.Payload,
                        ErrorCategory =
                            ErrorCategory.ValidationFailure,
                        ErrorMessage = errorMessage,
                        AttemptCount = receiveCount,
                        DeadLetteredAt = DateTime.UtcNow,
                        Status = DeadLetterStatus.Pending
                    },
                    cancellationToken);
            }
            else if (receiveCount > existing.AttemptCount)
            {
                existing.AttemptCount = receiveCount;
                existing.ErrorMessage = errorMessage;
            }

            var jobRun = await jobRunRepo.GetByIdAsync(
                ResolveJobRunId(message.MessageId),
                cancellationToken);
            jobRun?.MarkDeadLettered(DateTime.UtcNow);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogError(
                "Poison message {MessageId} recorded for SQS DLQ after receive {ReceiveCount}/{MaxReceiveCount}",
                message.MessageId,
                receiveCount,
                MaxReceiveCount);
        }
        finally
        {
            TenantFilterAccessor.Clear();
        }
    }

    private async Task HeartbeatLoop(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatIntervalMs, cancellationToken);
                using var heartbeatScope =
                    serviceProvider.CreateScope();
                var leaseRepo = heartbeatScope.ServiceProvider
                    .GetRequiredService<ILeaseRepository>();
                await leaseRepo.RenewAsync(
                    leaseId,
                    DateTime.UtcNow.AddSeconds(LeaseSeconds),
                    cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }
}
