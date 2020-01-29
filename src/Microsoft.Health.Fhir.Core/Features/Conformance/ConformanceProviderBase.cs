﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Hl7.FhirPath;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Conformance
{
    public abstract class ConformanceProviderBase : IConformanceProvider
    {
        private readonly ConcurrentDictionary<string, bool> _evaluatedQueries = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public abstract Task<ResourceElement> GetCapabilityStatementAsync(CancellationToken cancellationToken = default(CancellationToken));

        public async Task<bool> SatisfiesAsync(IEnumerable<CapabilityQuery> queries, CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureArg.IsNotNull(queries, nameof(queries));

            ResourceElement capabilityStatement = await GetCapabilityStatementAsync(cancellationToken);

            return queries.All(x => _evaluatedQueries.GetOrAdd(x.FhirPathPredicate, _ => capabilityStatement.Instance.Predicate(x.FhirPathPredicate)));
        }
    }
}
