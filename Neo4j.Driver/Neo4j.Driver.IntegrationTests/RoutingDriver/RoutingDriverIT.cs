﻿// Copyright (c) 2002-2017 "Neo Technology,"
// Network Engine for Objects in Lund AB [http://neotechnology.com]
// 
// This file is part of Neo4j.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Neo4j.Driver.IntegrationTests.Internals;
using Neo4j.Driver.Internal;
using Neo4j.Driver.V1;
using Xunit;
using Xunit.Abstractions;

namespace Neo4j.Driver.IntegrationTests
{
    [Collection(CCIntegrationCollection.CollectionName)]
    public class RoutingDriverIT : IDisposable
    {
        public static readonly Config DebugConfig = Config.Builder.WithLogger(new DebugLogger { Level = LogLevel.Debug }).ToConfig();
        protected ITestOutputHelper Output { get; }
        protected CausalCluster Cluster { get; }
        protected IAuthToken AuthToken { get; }

        private string RoutingServer => Cluster.AnyCore().BoltRoutingUri.ToString();
        private string WrongServer => "bolt+routing://localhost:1234";

        public RoutingDriverIT(ITestOutputHelper output, CausalClusterIntegrationTestFixture fixture)
        {
            Output = output;
            Cluster = fixture.Cluster;
            AuthToken = Cluster.AuthToken;
        }

        public void Dispose()
        {
            // put some code that you want to run after each unit test
        }

        [RequireClusterFact]
        public void ShouldFailWithAuthenticationError()
        {
            Exception exception = null;
            using (var driver = GraphDatabase.Driver(RoutingServer, AuthTokens.Basic("fake", "fake")))
            using(var session = driver.Session())
            {
                exception = Record.Exception(() => session.Run("RETURN 1"));
            }
            exception.Should().BeOfType<AuthenticationException>();
            exception.Message.Should().Be("The client is unauthorized due to authentication failure.");
        }


        [RequireClusterFact]
        public void ShouldConnectClusterWithRoutingScheme()
        {
            using (var driver = GraphDatabase.Driver(RoutingServer, AuthToken))
            using (var session = driver.Session())
            {
                var result = session.Run("UNWIND range(1,10000) AS x CREATE (n {prop:x}) DELETE n RETURN sum(x)");
                result.Single()[0].ValueAs<int>().Should().Be(10001 * 10000 / 2);
            }
        }

        [RequireClusterFact]
        public void ShouldLoadBalanceBetweenServers()
        {
            using (var driver = GraphDatabase.Driver(RoutingServer, AuthToken))
            {
                string addr1, addr2;
                for (int i = 0; i < 10; i++)
                {
                    using (var session = driver.Session(AccessMode.Read))
                    {
                        var result = session.Run("RETURN 1");
                        addr1 = result.Summary.Server.Address;
                    }
                    using (var session = driver.Session(AccessMode.Read))
                    {
                        addr2 = session.Run("RETURN 2").Summary.Server.Address;
                    }
                    addr1.Should().NotBe(addr2);
                }
            }
        }

        [RequireClusterFact]
        public void ShouldThrowServiceUnavailableExceptionIfNoServer()
        {
            Exception error = null;
            using (var driver = GraphDatabase.Driver(WrongServer, AuthTokens.Basic("fake", "fake")))
            using (var session = driver.Session())
            {
                error = Record.Exception(() => session.Run("RETURN 1"));
            }
            error.Should().BeOfType<ServiceUnavailableException>();
            error.Message.Should().Be("Failed to connect to any routing server. Please make sure that the cluster is up and can be accessed by the driver and retry.");
        }

        [RequireClusterFact]
        public void ShouldDisallowMoreStatementAfterDriverDispose()
        {
            var driver = GraphDatabase.Driver(RoutingServer, AuthToken);
            var session = driver.Session(AccessMode.Write);
            session.Run("RETURN 1").Single()[0].ValueAs<int>().Should().Be(1);

            driver.Dispose();
            var error = Record.Exception(() => session.Run("RETURN 1"));
            error.Should().BeOfType<ObjectDisposedException>();
            error.Message.Should().StartWith("Failed to acquire a new connection as the driver has already been disposed.");
        }

        [RequireClusterFact]
        public void ShouldDisallowMoreConnectionsAfterDriverDispose()
        {
            var driver = GraphDatabase.Driver(RoutingServer, AuthToken);
            var session = driver.Session(AccessMode.Write);
            session.Run("RETURN 1").Single()[0].ValueAs<int>().Should().Be(1);

            driver.Dispose();
            session.Dispose();

            var error = Record.Exception(() => driver.Session());
            error.Should().BeOfType<ObjectDisposedException>();
            error.Message.Should().Contain("Cannot open a new session on a driver that is already disposed.");
        }

        [RequireClusterTheory]
        [InlineData(10)]
        [InlineData(100)]
        [InlineData(1000)]
        public void SoakRunTests(int threadCount)
        {
            var driver = GraphDatabase.Driver(RoutingServer, AuthToken);
            var random = new Random();
            var job = new Job(driver, random, Output);

            var threads= new Thread[threadCount];
            for (int j = 0; j < threadCount; j++)
            {
                var thread = new Thread(job.Execute);
                Thread.Sleep(random.Next(100)); // sleep for sometime
                threads[j] = thread;
                thread.Start();
            }
            for (int i = 0; i < threadCount; i++)
            {
               threads[i].Join(); // wait for each thread to finish
            }

            driver.Dispose();
        }

        public class Job
        {
            private readonly IDriver _driver;
            private static readonly AccessMode[] Access = {AccessMode.Read, AccessMode.Write};
            private static readonly string[] Queries = { "RETURN 1295 + 42", "UNWIND range(1,10000) AS x CREATE (n {prop:x}) DELETE n RETURN sum(x)" };
            private readonly Random _random;
            private readonly ITestOutputHelper _output;

            public Job(IDriver driver, Random random, ITestOutputHelper output)
            {
                _driver = driver;
                _random = random;
                _output = output;
            }

            public void Execute()
            {
                var i = _random.Next(2);
                ISession session = null;
                try
                {
                    session = _driver.Session(Access[i]);
                    var result = session.Run(Queries[i]);
                    switch (i)
                    {
                        case 0:
                            result.Single()[0].ValueAs<int>().Should().Be(1337);
                            break;
                        case 1:
                            result.Single()[0].ValueAs<int>().Should().Be(10001 * 10000 / 2);
                            break;
                    }
                }
                catch (Exception e)
                {
                    _output.WriteLine($"Failed to run query {Queries[i]} due to {e.Message}");
                    e.Should().BeOfType<SessionExpiredException>();
                    e.Message.Should().Contain("no longer accepts writes");
                }
                finally
                {
                    session?.Dispose();
                }
            }
        }
    }
}