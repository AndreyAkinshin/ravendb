// -----------------------------------------------------------------------
//  <copyright file="RavenDB-3472.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Document;
using Raven.Client.Indexes;
using Raven.Client.Shard;
using Raven.Tests.Helpers;
using Xunit;

namespace Raven.Tests.Issues
{
    public class ShardingTransformerTests_Async : RavenTestBase
    {
        [Fact]
        public async Task TransformerOverShardedLoad_IdIsNotNull()
        {
            using (var server = GetNewServer())
            {
                var shards = new Dictionary<string, IDocumentStore>
                {
                    {"Asia", new DocumentStore {Url = server.Configuration.ServerUrl, DefaultDatabase = "Asia3"}},
                    {"Middle-East", new DocumentStore {Url = server.Configuration.ServerUrl, DefaultDatabase = "MiddleEast3"}},
                    {"America", new DocumentStore {Url = server.Configuration.ServerUrl, DefaultDatabase = "America3"}},
                };

                ShardStrategy shardStrategy = new ShardStrategy(shards)
                    .ShardingOn<Company>(company => company.Region)
                    .ShardingOn<Invoice>(x => x.CompanyId);

                using (IDocumentStore store = new ShardedDocumentStore(shardStrategy))
                {

                    store.Initialize();

                    new Transformer().Execute(store);

                    string americanCompanyId;
                    using (var session = store.OpenAsyncSession())
                    {
                        Company asian = new Company { Name = "Company 1", Region = "Asia" };
                        await session.StoreAsync(asian);
                        Company middleEastern = new Company { Name = "Company 2", Region = "Middle-East" };
                        await session.StoreAsync(middleEastern);
                        Company american = new Company { Name = "Company 3", Region = "America" };
                        await session.StoreAsync(american);

                        await session.StoreAsync(new Invoice { CompanyId = american.Id, Amount = 3, IssuedAt = DateTime.Today.AddDays(-1) });
                        await session.StoreAsync(new Invoice { CompanyId = asian.Id, Amount = 5, IssuedAt = DateTime.Today.AddDays(-1) });
                        await session.StoreAsync(new Invoice { CompanyId = middleEastern.Id, Amount = 12, IssuedAt = DateTime.Today });
                        await session.SaveChangesAsync();

                        americanCompanyId = american.Id;
                    }

                    using (var session = store.OpenAsyncSession())
                    {
                        var company = await session.LoadAsync<Transformer, Transformer.Result>(americanCompanyId);

                        Assert.Equal(company.Id, americanCompanyId);
                    }
                }
            }
        }

        [Fact]
        public async Task TransformerOverShardedLoad_IdIsNotNull_WithCustomerStrategy()
        {
            using (var server = GetNewServer())
            {
                var shards = new Dictionary<string, IDocumentStore>
                {
                    {"Shard0", new DocumentStore {Url = server.Configuration.ServerUrl, DefaultDatabase = "2Shard0"}},
                    {"Shard1", new DocumentStore {Url = server.Configuration.ServerUrl, DefaultDatabase = "2Shard1"}},
                    {"Shard2", new DocumentStore {Url = server.Configuration.ServerUrl, DefaultDatabase = "2Shard2"}},
           };

                var shardStrategy = new ShardStrategy(shards) {ShardResolutionStrategy = new ShardResolutionStrategy()};

                using (IDocumentStore store = new ShardedDocumentStore(shardStrategy))
                {

                    store.Initialize();

                    new Transformer().Execute(store);

                    string americanCompanyId;
                    using (var session = store.OpenAsyncSession())
                    {

                        Company asian = new Company { Name = "Company 1" };
                        await session.StoreAsync(asian);
                        Company middleEastern = new Company { Name = "Company 2" };
                        await session.StoreAsync(middleEastern);
                        Company american = new Company { Name = "Company 3" };
                        await session.StoreAsync(american);

                        await session.SaveChangesAsync();
 

                        americanCompanyId = american.Id;
                    }

                    using (var session = store.OpenAsyncSession())
                    {
                        var company = await session.LoadAsync<Transformer, Transformer.Result>(americanCompanyId);

                        Assert.Equal(company.Id, americanCompanyId);
                    }
                }
            }
        }

        [Fact]
        public async Task TransformerOverLoad_IdIsNotNull()
        {
            using (IDocumentStore store = NewDocumentStore())
            {

                store.Initialize();

                new Transformer().Execute(store);

                string americanCompanyId;
                using (var session = store.OpenAsyncSession())
                {
                    Company american = new Company { Name = "Company 3", Region = "America" };
                    await session.StoreAsync(american);

                    await session.StoreAsync(new Invoice { CompanyId = american.Id, Amount = 3, IssuedAt = DateTime.Today.AddDays(-1) });
                    await session.SaveChangesAsync();

                    americanCompanyId = american.Id;
                }

                using (var session = store.OpenAsyncSession())
                {
                    var company = await session.LoadAsync<Transformer, Transformer.Result>(americanCompanyId);

                    Assert.Equal(company.Id, americanCompanyId);
                }
            }
        }

        public class ShardResolutionStrategy : IShardResolutionStrategy
        {
            public string GenerateShardIdFor(object entity, object sessionMetadata)
            {
                if (entity is Company)
                {
                    var company = (Company)entity;
                    return "Shard" + Math.Abs(company.Id.GetHashCode()) % 3;
                }
                else
                    throw new ArgumentOutOfRangeException("Unexpected entity");
            }

            public string MetadataShardIdFor(object entity)
            {
                return "Shard0";
            }

            public IList<string> PotentialShardsFor(ShardRequestData requestData)
            {
                return new[] { "Shard0", "Shard1", "Shard2" };
            }
        }
        public class Transformer : AbstractTransformerCreationTask<Company>
        {
            public class Result
            {
                public string Id { get; set; }
                public string Name { get; set; }
            }

            public Transformer()
            {
                TransformResults = companies => from company in companies
                                                select new
                                                {
                                                    Id = company.Id,
                                                    Name = company.Name
                                                };
            }
        }

        public class Company
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Region { get; set; }
        }

        public class Invoice
        {
            public string Id { get; set; }
            public string CompanyId { get; set; }
            public decimal Amount { get; set; }
            public DateTime IssuedAt { get; set; }
        }
    }

}
