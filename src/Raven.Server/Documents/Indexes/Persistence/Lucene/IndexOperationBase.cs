﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Lucene.Net.Analysis;
using Lucene.Net.Search;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Analyzers;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Logging;
using Query = Lucene.Net.Search.Query;

namespace Raven.Server.Documents.Indexes.Persistence.Lucene
{
    public abstract class IndexOperationBase : IDisposable
    {
        private static readonly ConcurrentDictionary<Type, bool> NotForQuerying = new ConcurrentDictionary<Type, bool>();

        protected readonly string _indexName;

        protected readonly Logger _logger;
        internal Index _index;

        protected IndexOperationBase(Index index, Logger logger)
        {
            _index = index;
            _indexName = index.Name;
            _logger = logger;
        }

        protected static RavenPerFieldAnalyzerWrapper CreateAnalyzer(Func<Analyzer> createDefaultAnalyzer, Dictionary<string, IndexField> fields, bool forQuerying = false)
        {
            if (fields.ContainsKey(Constants.Documents.Indexing.Fields.AllFields))
                throw new InvalidOperationException($"Detected '{Constants.Documents.Indexing.Fields.AllFields}'. This field should not be present here, because inheritance is done elsewhere.");

            var defaultAnalyzer = createDefaultAnalyzer();

            RavenStandardAnalyzer standardAnalyzer = null;
            KeywordAnalyzer keywordAnalyzer = null;
            var perFieldAnalyzerWrapper = new RavenPerFieldAnalyzerWrapper(defaultAnalyzer);
            foreach (var field in fields)
            {
                var fieldName = field.Value.Name;

                switch (field.Value.Indexing)
                {
                    case FieldIndexing.Exact:
                        if (keywordAnalyzer == null)
                            keywordAnalyzer = new KeywordAnalyzer();

                        perFieldAnalyzerWrapper.AddAnalyzer(fieldName, keywordAnalyzer);
                        break;
                    case FieldIndexing.Search:
                        var analyzer = GetAnalyzer(fieldName, field.Value, forQuerying);
                        if (analyzer != null)
                        {
                            perFieldAnalyzerWrapper.AddAnalyzer(fieldName, analyzer);
                            continue;
                        }
                        AddStandardAnalyzer(fieldName);

                        break;
                }
            }

            return perFieldAnalyzerWrapper;

            void AddStandardAnalyzer(string fieldName)
            {
                if (standardAnalyzer == null)
                    standardAnalyzer = new RavenStandardAnalyzer(global::Lucene.Net.Util.Version.LUCENE_29);

                perFieldAnalyzerWrapper.AddAnalyzer(fieldName, standardAnalyzer);
            }
        }

        public abstract void Dispose();

        private static Analyzer GetAnalyzer(string name, IndexField field, bool forQuerying)
        {
            if (string.IsNullOrWhiteSpace(field.Analyzer))
                return null;

            var analyzerInstance = IndexingExtensions.CreateAnalyzerInstance(name, field.Analyzer);

            if (forQuerying)
            {
                var analyzerType = analyzerInstance.GetType();

                var notForQuerying = NotForQuerying
                    .GetOrAdd(analyzerType, t => analyzerInstance.GetType().GetTypeInfo().GetCustomAttributes<NotForQueryingAttribute>(false).Any());

                if (notForQuerying)
                    return null;
            }

            return analyzerInstance;
        }

        protected Query GetLuceneQuery(DocumentsOperationContext context, QueryMetadata metadata, BlittableJsonReaderObject parameters, Analyzer analyzer, QueryBuilderFactories factories)
        {
            return GetLuceneQuery(context, metadata, metadata.Query.Where, parameters, analyzer, factories);
        }

        protected Query GetLuceneQuery(DocumentsOperationContext context, QueryMetadata metadata, QueryExpression whereExpression, BlittableJsonReaderObject parameters, Analyzer analyzer, QueryBuilderFactories factories)
        {
            Query documentQuery;

            if (string.IsNullOrEmpty(metadata.QueryText))
            {
                if (_logger.IsInfoEnabled)
                    _logger.Info($"Issuing query on index {_indexName} for all documents");

                documentQuery = new MatchAllDocsQuery();
            }
            else
            {
                if (_logger.IsInfoEnabled)
                    _logger.Info($"Issuing query on index {_indexName} for: {metadata.Query}");

                // RavenPerFieldAnalyzerWrapper searchAnalyzer = null;
                try
                {
                    //_persistance._a
                    //searchAnalyzer = parent.CreateAnalyzer(new LowerCaseKeywordAnalyzer(), toDispose, true);
                    //searchAnalyzer = parent.AnalyzerGenerators.Aggregate(searchAnalyzer, (currentAnalyzer, generator) =>
                    //{
                    //    Analyzer newAnalyzer = generator.GenerateAnalyzerForQuerying(parent.PublicName, query.Query, currentAnalyzer);
                    //    if (newAnalyzer != currentAnalyzer)
                    //    {
                    //        DisposeAnalyzerAndFriends(toDispose, currentAnalyzer);
                    //    }
                    //    return parent.CreateAnalyzer(newAnalyzer, toDispose, true);
                    //});

                    IDisposable releaseServerContext = null;
                    IDisposable closeServerTransaction = null;
                    TransactionOperationContext serverContext = null;

                    try
                    {
                        if (metadata.HasCmpXchg)
                        {
                            releaseServerContext = context.DocumentDatabase.ServerStore.ContextPool.AllocateOperationContext(out serverContext);
                            closeServerTransaction = serverContext.OpenReadTransaction();
                        }

                        using (closeServerTransaction)
                            documentQuery = QueryBuilder.BuildQuery(serverContext, context, metadata, whereExpression, parameters, analyzer, factories);
                    }
                    finally
                    {
                        releaseServerContext?.Dispose();
                    }
                }
                finally
                {
                    //DisposeAnalyzerAndFriends(toDispose, searchAnalyzer);
                }
            }

            //var afterTriggers = ApplyIndexTriggers(documentQuery);

            return documentQuery;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static int GetPageSize(IndexSearcher searcher, long pageSize)
        {
            if (pageSize >= searcher.MaxDoc)
                return searcher.MaxDoc;

            if (pageSize >= int.MaxValue)
                return int.MaxValue;

            return (int)pageSize;
        }
    }
}
