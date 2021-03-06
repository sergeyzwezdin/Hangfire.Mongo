﻿using System;
using System.Linq;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.Tests.Utils;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Hangfire.Mongo.Tests
{
#pragma warning disable 1591
    [Collection("Database")]
    public class MongoFetchedJobFacts
    {
        private static readonly ObjectId JobId = ObjectId.GenerateNewId();
        private const string Queue = "queue";
        private readonly HangfireDbContext _dbContext = ConnectionUtils.CreateDbContext();

        [Fact]
        public void Ctor_ThrowsAnException_WhenConnectionIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new MongoFetchedJob(null, ObjectId.GenerateNewId(), JobId, Queue));

            Assert.Equal("connection", exception.ParamName);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenQueueIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new MongoFetchedJob(_dbContext, ObjectId.GenerateNewId(), JobId, null));

            Assert.Equal("queue", exception.ParamName);
        }

        [Fact]
        public void Ctor_CorrectlySets_AllInstanceProperties()
        {
            var fetchedJob = new MongoFetchedJob(_dbContext, ObjectId.GenerateNewId(), JobId, Queue);

            Assert.Equal(JobId.ToString(), fetchedJob.JobId);
            Assert.Equal(Queue, fetchedJob.Queue);
        }

        [Fact, CleanDatabase]
        public void RemoveFromQueue_ReallyDeletesTheJobFromTheQueue()
        {
            // Arrange
            var queue = "default";
            var jobId = ObjectId.GenerateNewId();
            var id = CreateJobQueueRecord(_dbContext, jobId, queue);
            var processingJob = new MongoFetchedJob(_dbContext, id, jobId, queue);

            // Act
            processingJob.RemoveFromQueue();

            // Assert
            var count = _dbContext.JobGraph.OfType<JobQueueDto>().Count(new BsonDocument());
            Assert.Equal(0, count);
        }

        [Fact, CleanDatabase]
        public void RemoveFromQueue_DoesNotDelete_UnrelatedJobs()
        {
            // Arrange
            CreateJobQueueRecord(_dbContext, ObjectId.GenerateNewId(1), "default");
            CreateJobQueueRecord(_dbContext, ObjectId.GenerateNewId(2), "critical");
            CreateJobQueueRecord(_dbContext, ObjectId.GenerateNewId(3), "default");

            var fetchedJob = new MongoFetchedJob(_dbContext, ObjectId.GenerateNewId(), ObjectId.GenerateNewId(999), "default");

            // Act
            fetchedJob.RemoveFromQueue();

            // Assert
            var count = _dbContext.JobGraph.OfType<JobQueueDto>().Count(new BsonDocument());
            Assert.Equal(3, count);
        }

        [Fact, CleanDatabase]
        public void Requeue_SetsFetchedAtValueToNull()
        {
            // Arrange
            var queue = "default";
            var jobId = ObjectId.GenerateNewId();
            var id = CreateJobQueueRecord(_dbContext, jobId, queue);
            var processingJob = new MongoFetchedJob(_dbContext, id, jobId, queue);

            // Act
            processingJob.Requeue();

            // Assert
            var record = _dbContext.JobGraph.OfType<JobQueueDto>().Find(new BsonDocument()).ToList().Single();
            Assert.Null(record.FetchedAt);
        }

        [Fact, CleanDatabase]
        public void Dispose_SetsFetchedAtValueToNull_IfThereWereNoCallsToComplete()
        {
            // Arrange
            var queue = "default";
            var jobId = ObjectId.GenerateNewId();
            var id = CreateJobQueueRecord(_dbContext, jobId, queue);
            var processingJob = new MongoFetchedJob(_dbContext, id, jobId, queue);

            // Act
            processingJob.Dispose();

            // Assert
            var record = _dbContext.JobGraph.OfType<JobQueueDto>().Find(new BsonDocument()).ToList().Single();
            Assert.Null(record.FetchedAt);
        }

        private static ObjectId CreateJobQueueRecord(HangfireDbContext connection, ObjectId jobId, string queue)
        {
            var jobQueue = new JobQueueDto
            {
                Id = ObjectId.GenerateNewId(),
                JobId = jobId,
                Queue = queue,
                FetchedAt = DateTime.UtcNow
            };

            connection.JobGraph.InsertOne(jobQueue);

            return jobQueue.Id;
        }
    }
#pragma warning restore 1591
}