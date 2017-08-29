﻿using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire.Common;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.PersistentJobQueue;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using MongoDB.Bson;
using MongoDB.Driver;
using ServerDto = Hangfire.Storage.Monitoring.ServerDto;

namespace Hangfire.Mongo
{
#pragma warning disable 1591
    public class MongoMonitoringApi : IMonitoringApi
    {
        private readonly HangfireDbContext _database;

        private readonly PersistentJobQueueProviderCollection _queueProviders;

        public MongoMonitoringApi(HangfireDbContext database, PersistentJobQueueProviderCollection queueProviders)
        {
            _database = database;
            _queueProviders = queueProviders;
        }

        public IList<QueueWithTopEnqueuedJobsDto> Queues()
        {
            return UseConnection<IList<QueueWithTopEnqueuedJobsDto>>(database =>
            {
                var tuples = _queueProviders
                    .Select(x => x.GetJobQueueMonitoringApi(database))
                    .SelectMany(x => x.GetQueues(), (monitoring, queue) => new { Monitoring = monitoring, Queue = queue })
                    .OrderBy(x => x.Queue)
                    .ToArray();

                var result = new List<QueueWithTopEnqueuedJobsDto>(tuples.Length);

                foreach (var tuple in tuples)
                {
                    var enqueuedJobIds = tuple.Monitoring.GetEnqueuedJobIds(tuple.Queue, 0, 5);
                    var counters = tuple.Monitoring.GetEnqueuedAndFetchedCount(tuple.Queue);

                    result.Add(new QueueWithTopEnqueuedJobsDto
                    {
                        Name = tuple.Queue,
                        Length = counters.EnqueuedCount ?? 0,
                        Fetched = counters.FetchedCount,
                        FirstJobs = EnqueuedJobs(database, enqueuedJobIds)
                    });
                }

                return result;
            });
        }

        public IList<ServerDto> Servers()
        {
            return UseConnection<IList<ServerDto>>(database =>
            {
                var servers = database.Server.Find(new BsonDocument()).ToList();

                var result = new List<ServerDto>();

                foreach (var server in servers)
                {
                    var data = JobHelper.FromJson<ServerDataDto>(server.Data);
                    result.Add(new ServerDto
                    {
                        Name = server.Id,
                        Heartbeat = server.LastHeartbeat,
                        Queues = data.Queues,
                        StartedAt = data.StartedAt ?? DateTime.MinValue,
                        WorkersCount = data.WorkerCount
                    });
                }

                return result;
            });
        }

        public JobDetailsDto JobDetails(string jobId)
        {
            return UseConnection(database =>
            {
                JobDto job = database.Job.Find(Builders<JobDto>.Filter.Eq(_ => _.Id, jobId))
                    .FirstOrDefault();

                if (job == null)
                    return null;

                var history = job.StateHistory.Select(x => new StateHistoryDto
                {
                    StateName = x.Name,
                    CreatedAt = x.CreatedAt,
                    Reason = x.Reason,
                    Data = x.Data
                }).ToList();

                return new JobDetailsDto
                {
                    CreatedAt = job.CreatedAt,
                    Job = DeserializeJob(job.InvocationData, job.Arguments),
                    History = history,
                    Properties = job.Parameters
                };
            });
        }

        public StatisticsDto GetStatistics()
        {
            return UseConnection(database =>
            {
                var stats = new StatisticsDto();

                var countByStates = database.Job.Aggregate()
                    .Match(Builders<JobDto>.Filter.Ne(_ => _.StateName, null))
                    .Group(dto => new { dto.StateName }, dtos => new { StateName = dtos.First().StateName, Count = dtos.Count() })
                    .ToList().ToDictionary(kv => kv.StateName, kv => kv.Count);

                Func<string, int> getCountIfExists = name => countByStates.ContainsKey(name) ? countByStates[name] : 0;

                stats.Enqueued = getCountIfExists(EnqueuedState.StateName);
                stats.Failed = getCountIfExists(FailedState.StateName);
                stats.Processing = getCountIfExists(ProcessingState.StateName);
                stats.Scheduled = getCountIfExists(ScheduledState.StateName);

                stats.Servers = database.Server.Count(new BsonDocument());

                long[] succeededItems = database.StateData.OfType<CounterDto>().Find(Builders<CounterDto>.Filter.Eq(_ => _.Key, "stats:succeeded")).ToList().Select(_ => (long)_.Value)
                    .Concat(database.StateData.OfType<AggregatedCounterDto>().Find(Builders<AggregatedCounterDto>.Filter.Eq(_ => _.Key, "stats:succeeded")).ToList().Select(_ => (long)_.Value))
                    .ToArray();

                stats.Succeeded = succeededItems.Any() ? succeededItems.Sum() : 0;

                long[] deletedItems = database.StateData.OfType<CounterDto>().Find(Builders<CounterDto>.Filter.Eq(_ => _.Key, "stats:deleted")).ToList().Select(_ => (long)_.Value)
                    .Concat(database.StateData.OfType<AggregatedCounterDto>().Find(Builders<AggregatedCounterDto>.Filter.Eq(_ => _.Key, "stats:deleted")).ToList().Select(_ => (long)_.Value))
                    .ToArray();
                stats.Deleted = deletedItems.Any() ? deletedItems.Sum() : 0;

                stats.Recurring = database.StateData.OfType<SetDto>().Count(Builders<SetDto>.Filter.Eq(_ => _.Key, "recurring-jobs"));

                stats.Queues = _queueProviders
                    .SelectMany(x => x.GetJobQueueMonitoringApi(database).GetQueues())
                    .Count();

                return stats;
            });
        }

        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int from, int perPage)
        {
            return UseConnection(database =>
            {
                var queueApi = GetQueueApi(database, queue);
                var enqueuedJobIds = queueApi.GetEnqueuedJobIds(queue, from, perPage);

                return EnqueuedJobs(database, enqueuedJobIds);
            });
        }

        public JobList<FetchedJobDto> FetchedJobs(string queue, int from, int perPage)
        {
            return UseConnection(database =>
            {
                var queueApi = GetQueueApi(database, queue);
                var fetchedJobIds = queueApi.GetFetchedJobIds(queue, from, perPage);

                return FetchedJobs(database, fetchedJobIds);
            });
        }

        public JobList<ProcessingJobDto> ProcessingJobs(int from, int count)
        {
            return UseConnection(database => GetJobs(
                database,
                from, count,
                ProcessingState.StateName,
                (sqlJob, job, stateData) => new ProcessingJobDto
                {
                    Job = job,
                    ServerId = stateData.ContainsKey("ServerId") ? stateData["ServerId"] : stateData["ServerName"],
                    StartedAt = JobHelper.DeserializeDateTime(stateData["StartedAt"]),
                }));
        }

        public JobList<ScheduledJobDto> ScheduledJobs(int from, int count)
        {
            return UseConnection(database => GetJobs(database, from, count, ScheduledState.StateName,
                (sqlJob, job, stateData) => new ScheduledJobDto
                {
                    Job = job,
                    EnqueueAt = JobHelper.DeserializeDateTime(stateData["EnqueueAt"]),
                    ScheduledAt = JobHelper.DeserializeDateTime(stateData["ScheduledAt"])
                }));
        }

        public JobList<SucceededJobDto> SucceededJobs(int from, int count)
        {
            return UseConnection(database => GetJobs(database, from, count, SucceededState.StateName,
                (sqlJob, job, stateData) => new SucceededJobDto
                {
                    Job = job,
                    Result = stateData.ContainsKey("Result") ? stateData["Result"] : null,
                    TotalDuration = stateData.ContainsKey("PerformanceDuration") && stateData.ContainsKey("Latency")
                        ? (long?)long.Parse(stateData["PerformanceDuration"]) + (long?)long.Parse(stateData["Latency"])
                        : null,
                    SucceededAt = JobHelper.DeserializeNullableDateTime(stateData["SucceededAt"])
                }));
        }

        public JobList<FailedJobDto> FailedJobs(int from, int count)
        {
            return UseConnection(database => GetJobs(database, from, count, FailedState.StateName,
                (sqlJob, job, stateData) => new FailedJobDto
                {
                    Job = job,
                    Reason = sqlJob.StateReason,
                    ExceptionDetails = stateData["ExceptionDetails"],
                    ExceptionMessage = stateData["ExceptionMessage"],
                    ExceptionType = stateData["ExceptionType"],
                    FailedAt = JobHelper.DeserializeNullableDateTime(stateData["FailedAt"])
                }));
        }

        public JobList<DeletedJobDto> DeletedJobs(int from, int count)
        {
            return UseConnection(database => GetJobs(database, from, count, DeletedState.StateName,
                (sqlJob, job, stateData) => new DeletedJobDto
                {
                    Job = job,
                    DeletedAt = JobHelper.DeserializeNullableDateTime(stateData["DeletedAt"])
                }));
        }

        public long ScheduledCount()
        {
            return UseConnection(database => GetNumberOfJobsByStateName(database, ScheduledState.StateName));
        }

        public long EnqueuedCount(string queue)
        {
            return UseConnection(database =>
            {
                var queueApi = GetQueueApi(database, queue);
                var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

                return counters.EnqueuedCount ?? 0;
            });
        }

        public long FetchedCount(string queue)
        {
            return UseConnection(database =>
            {
                var queueApi = GetQueueApi(database, queue);
                var counters = queueApi.GetEnqueuedAndFetchedCount(queue);

                return counters.FetchedCount ?? 0;
            });
        }

        public long FailedCount()
        {
            return UseConnection(database => GetNumberOfJobsByStateName(database, FailedState.StateName));
        }

        public long ProcessingCount()
        {
            return UseConnection(database => GetNumberOfJobsByStateName(database, ProcessingState.StateName));
        }

        public long SucceededListCount()
        {
            return UseConnection(database => GetNumberOfJobsByStateName(database, SucceededState.StateName));
        }

        public long DeletedListCount()
        {
            return UseConnection(database => GetNumberOfJobsByStateName(database, DeletedState.StateName));
        }

        public IDictionary<DateTime, long> SucceededByDatesCount()
        {
            return UseConnection(database => GetTimelineStats(database, "succeeded"));
        }

        public IDictionary<DateTime, long> FailedByDatesCount()
        {
            return UseConnection(database => GetTimelineStats(database, "failed"));
        }

        public IDictionary<DateTime, long> HourlySucceededJobs()
        {
            return UseConnection(database => GetHourlyTimelineStats(database, "succeeded"));
        }

        public IDictionary<DateTime, long> HourlyFailedJobs()
        {
            return UseConnection(database => GetHourlyTimelineStats(database, "failed"));
        }

        private T UseConnection<T>(Func<HangfireDbContext, T> action)
        {
            var result = action(_database);
            return result;
        }

        private JobList<EnqueuedJobDto> EnqueuedJobs(HangfireDbContext database, IEnumerable<string> jobIds)
        {
            var jobs = database.Job
                .Find(Builders<JobDto>.Filter.In(_ => _.Id, jobIds))
                .ToList();

            var filterBuilder = Builders<JobQueueDto>.Filter;
            var enqueuedJobs = database.JobQueue
                .Find(filterBuilder.In(_ => _.JobId, jobs.Select(job => job.Id)) &
                      (filterBuilder.Not(filterBuilder.Exists(_ => _.FetchedAt)) | filterBuilder.Eq(_ => _.FetchedAt, null)))
                .ToList();

            var jobsFiltered = enqueuedJobs
                .Select(jq => jobs.FirstOrDefault(job => job.Id == jq.JobId));

            var joinedJobs = jobsFiltered
                .Where(job => job != null)
                .Select(job =>
                {
                    var state = job.StateHistory.LastOrDefault();
                    return new JobDetailedDto
                    {
                        Id = job.Id,
                        InvocationData = job.InvocationData,
                        Arguments = job.Arguments,
                        CreatedAt = job.CreatedAt,
                        ExpireAt = job.ExpireAt,
                        FetchedAt = null,
                        StateName = job.StateName,
                        StateReason = state?.Reason,
                        StateData = state?.Data
                    };
                })
                .ToList();

            return DeserializeJobs(
                joinedJobs,
                (sqlJob, job, stateData) => new EnqueuedJobDto
                {
                    Job = job,
                    State = sqlJob.StateName,
                    EnqueuedAt = sqlJob.StateName == EnqueuedState.StateName
                        ? JobHelper.DeserializeNullableDateTime(stateData["EnqueuedAt"])
                        : null
                });
        }

        private static JobList<TDto> DeserializeJobs<TDto>(ICollection<JobDetailedDto> jobs, Func<JobDetailedDto, Job, Dictionary<string, string>, TDto> selector)
        {
            var result = new List<KeyValuePair<string, TDto>>(jobs.Count);

            foreach (var job in jobs)
            {
                var stateData = job.StateData;
                var dto = selector(job, DeserializeJob(job.InvocationData, job.Arguments), stateData);
                result.Add(new KeyValuePair<string, TDto>(job.Id, dto));
            }

            return new JobList<TDto>(result);
        }

        private static Job DeserializeJob(string invocationData, string arguments)
        {
            var data = JobHelper.FromJson<InvocationData>(invocationData);
            data.Arguments = arguments;

            try
            {
                return data.Deserialize();
            }
            catch (JobLoadException)
            {
                return null;
            }
        }

        private IPersistentJobQueueMonitoringApi GetQueueApi(HangfireDbContext database, string queueName)
        {
            var provider = _queueProviders.GetProvider(queueName);
            var monitoringApi = provider.GetJobQueueMonitoringApi(database);

            return monitoringApi;
        }

        private JobList<FetchedJobDto> FetchedJobs(HangfireDbContext database, IEnumerable<string> jobIds)
        {
            var jobs = database.Job
                .Find(Builders<JobDto>.Filter.In(_ => _.Id, jobIds))
                .ToList();

            var jobIdToJobQueueMap = database.JobQueue
                .Find(Builders<JobQueueDto>.Filter.In(_ => _.JobId, jobs.Select(job => job.Id))
                      & Builders<JobQueueDto>.Filter.Exists(_ => _.FetchedAt)
                      & Builders<JobQueueDto>.Filter.Not(Builders<JobQueueDto>.Filter.Eq(_ => _.FetchedAt, null)))
                .ToList().ToDictionary(kv => kv.JobId, kv => kv);

            IEnumerable<JobDto> jobsFiltered = jobs.Where(job => jobIdToJobQueueMap.ContainsKey(job.Id));

            List<JobDetailedDto> joinedJobs = jobsFiltered
                .Select(job =>
                {
                    var state = job.StateHistory.FirstOrDefault(s => s.Name == job.StateName);
                    return new JobDetailedDto
                    {
                        Id = job.Id,
                        InvocationData = job.InvocationData,
                        Arguments = job.Arguments,
                        CreatedAt = job.CreatedAt,
                        ExpireAt = job.ExpireAt,
                        FetchedAt = null,
                        StateName = job.StateName,
                        StateReason = state?.Reason,
                        StateData = state?.Data
                    };
                })
                .ToList();

            var result = new List<KeyValuePair<string, FetchedJobDto>>(joinedJobs.Count);

            foreach (var job in joinedJobs)
            {
                result.Add(new KeyValuePair<string, FetchedJobDto>(
                    job.Id,
                    new FetchedJobDto
                    {
                        Job = DeserializeJob(job.InvocationData, job.Arguments),
                        State = job.StateName,
                        FetchedAt = job.FetchedAt
                    }));
            }

            return new JobList<FetchedJobDto>(result);
        }

        private JobList<TDto> GetJobs<TDto>(HangfireDbContext database, int from, int count, string stateName, Func<JobDetailedDto, Job, Dictionary<string, string>, TDto> selector)
        {
            // only retrieve job ids
            var filter = Builders<JobDto>
                .Filter
                .Eq(j => j.StateName, stateName);

            var jobs = database.Job
                .Find(filter)
                .Skip(from)
                .Limit(count)
                .ToList();

            List<JobDetailedDto> joinedJobs = jobs
                .Select(job =>
                {
                    var state = job.StateHistory.FirstOrDefault(s => s.Name == stateName);

                    return new JobDetailedDto
                    {
                        Id = job.Id,
                        InvocationData = job.InvocationData,
                        Arguments = job.Arguments,
                        CreatedAt = job.CreatedAt,
                        ExpireAt = job.ExpireAt,
                        FetchedAt = null,
                        StateName = job.StateName,
                        StateReason = state?.Reason,
                        StateData = state?.Data
                    };
                })
                .ToList();

            return DeserializeJobs(joinedJobs, selector);
        }

        private long GetNumberOfJobsByStateName(HangfireDbContext database, string stateName)
        {
            var count = database.Job.Count(Builders<JobDto>.Filter.Eq(_ => _.StateName, stateName));
            return count;
        }

        private Dictionary<DateTime, long> GetTimelineStats(HangfireDbContext database, string type)
        {
            var endDate = DateTime.UtcNow.Date;
            var startDate = endDate.AddDays(-7);
            var dates = new List<DateTime>();

            while (startDate <= endDate)
            {
                dates.Add(endDate);
                endDate = endDate.AddDays(-1);
            }

            var stringDates = dates.Select(x => x.ToString("yyyy-MM-dd")).ToList();
            var keys = stringDates.Select(x => $"stats:{type}:{x}").ToList();

            var valuesMap = database.StateData.OfType<AggregatedCounterDto>()
                .Find(Builders<AggregatedCounterDto>.Filter.In(_ => _.Key, keys))
                .ToList()
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, x => (long)x.Count());

            foreach (var key in keys)
            {
                if (!valuesMap.ContainsKey(key)) valuesMap.Add(key, 0);
            }

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < stringDates.Count; i++)
            {
                var value = valuesMap[valuesMap.Keys.ElementAt(i)];
                result.Add(dates[i], value);
            }

            return result;
        }

        private Dictionary<DateTime, long> GetHourlyTimelineStats(HangfireDbContext database, string type)
        {
            var endDate = DateTime.UtcNow;
            var dates = new List<DateTime>();
            for (var i = 0; i < 24; i++)
            {
                dates.Add(endDate);
                endDate = endDate.AddHours(-1);
            }

            var keys = dates.Select(x => $"stats:{type}:{x:yyyy-MM-dd-HH}").ToList();

            var valuesMap = database.StateData.OfType<CounterDto>().Find(Builders<CounterDto>.Filter.In(_ => _.Key, keys))
                .ToList()
                .GroupBy(x => x.Key, x => x)
                .ToDictionary(x => x.Key, x => (long)x.Count());

            foreach (var key in keys.Where(key => !valuesMap.ContainsKey(key)))
            {
                valuesMap.Add(key, 0);
            }

            var result = new Dictionary<DateTime, long>();
            for (var i = 0; i < dates.Count; i++)
            {
                var value = valuesMap[valuesMap.Keys.ElementAt(i)];
                result.Add(dates[i], value);
            }

            return result;
        }
    }
#pragma warning restore 1591
}