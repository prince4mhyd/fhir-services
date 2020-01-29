﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Api.Features.Audit;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.E2E.Common;
using Xunit;
using FhirClient = Microsoft.Health.Fhir.Tests.E2E.Common.FhirClient;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Audit
{
    /// <summary>
    /// Provides Audit specific tests.
    /// </summary
    [HttpIntegrationFixtureArgumentSets(DataStore.CosmosDb, Format.Json)]
    public class AuditTests : IClassFixture<AuditTestFixture>
    {
        private const string RequestIdHeaderName = "X-Request-Id";
        private const string CustomAuditHeaderPrefix = "X-MS-AZUREFHIR-AUDIT-";
        private const string ExpectedClaimKey = "client_id";

        private readonly AuditTestFixture _fixture;
        private readonly FhirClient _client;

        private readonly TraceAuditLogger _auditLogger;

        public AuditTests(AuditTestFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.FhirClient;
            _auditLogger = _fixture.AuditLogger;
        }

        [Fact]
        public async Task GivenMetadata_WhenRead_ThenAuditLogEntriesShouldNotBeCreated()
        {
            if (!_fixture.IsUsingInProcTestServer)
            {
                // This test only works with the in-proc server with customized middleware pipeline
                return;
            }

            FhirResponse response = await _client.ReadAsync<CapabilityStatement>("metadata");

            string correlationId = response.Headers.GetValues(RequestIdHeaderName).FirstOrDefault();

            Assert.NotNull(correlationId);

            Assert.Empty(_auditLogger.GetAuditEntriesByCorrelationId(correlationId));
        }

        [Fact]
        public async Task GivenAResource_WhenCreated_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                () => _client.CreateAsync(Samples.GetDefaultObservation().ToPoco()),
                "create",
                ResourceType.Observation,
                _ => "Observation",
                HttpStatusCode.Created);
        }

        [Fact]
        public async Task GivenAnExistingResource_WhenRead_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                async () =>
                {
                    FhirResponse<Patient> response = await _client.CreateAsync(Samples.GetDefaultPatient().ToPoco<Patient>());

                    return await _client.ReadAsync<Patient>(ResourceType.Patient, response.Resource.Id);
                },
                "read",
                ResourceType.Patient,
                p => $"Patient/{p.Id}",
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenANonExistingResource_WhenRead_ThenAuditLogEntriesShouldBeCreated()
        {
            // TODO: The resource type being logged here is incorrect. The issue is tracked by https://github.com/Microsoft/fhir-server/issues/334.

            string resourceId = Guid.NewGuid().ToString();
            await ExecuteAndValidate(
                async () =>
                {
                    FhirResponse<OperationOutcome> result = null;

                    try
                    {
                        await _client.ReadAsync<Patient>(ResourceType.Patient, resourceId);
                    }
                    catch (FhirException ex)
                    {
                        result = ex.Response;
                    }

                    // The request should have failed.
                    Assert.NotNull(result);

                    return result;
                },
                "read",
                ResourceType.OperationOutcome,
                _ => $"Patient/{resourceId}",
                HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task GivenAnExistingResource_WhenReadAVersion_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                async () =>
                {
                    FhirResponse<Organization> result = await _client.CreateAsync(Samples.GetDefaultOrganization().ToPoco<Organization>());

                    return await _client.VReadAsync<Organization>(ResourceType.Organization, result.Resource.Id, result.Resource.Meta.VersionId);
                },
                "vread",
                ResourceType.Organization,
                o => $"Organization/{o.Id}/_history/{o.Meta.VersionId}",
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAnExistingResource_WhenUpdated_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                async () =>
                {
                    FhirResponse<Patient> result = await _client.CreateAsync(Samples.GetDefaultPatient().ToPoco<Patient>());

                    result.Resource.Name.Add(new HumanName() { Family = "Anderson" });

                    return await _client.UpdateAsync<Patient>(result);
                },
                "update",
                ResourceType.Patient,
                p => $"Patient/{p.Id}",
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAnExistingResource_WhenDeleted_ThenAuditLogEntriesShouldBeCreated()
        {
            if (!_fixture.IsUsingInProcTestServer)
            {
                // This test only works with the in-proc server with customized middleware pipeline
                return;
            }

            FhirResponse<Patient> result = await _client.CreateAsync(Samples.GetDefaultPatient().ToPoco<Patient>());

            FhirResponse deleteResult = await _client.DeleteAsync(result.Resource);

            string correlationId = deleteResult.Headers.GetValues(RequestIdHeaderName).First();

            var expectedUri = new Uri($"http://localhost/Patient/{result.Resource.Id}");

            string expectedAppId = TestApplications.ServiceClient.ClientId;

            // TODO: The resource type being logged here is incorrect. The issue is tracked by https://github.com/Microsoft/fhir-server/issues/334.
            Assert.Collection(
                _auditLogger.GetAuditEntriesByCorrelationId(correlationId),
                ae => ValidateExecutingAuditEntry(ae, "delete", expectedUri, correlationId, expectedAppId, ExpectedClaimKey),
                ae => ValidateExecutedAuditEntry(ae, "delete", null, expectedUri, HttpStatusCode.NoContent, correlationId, expectedAppId, ExpectedClaimKey));
        }

        [Fact]
        public async Task GivenAServer_WhenSearchedByResourceHistory_ThenAuditLogEntriesShouldBeCreated()
        {
            const string url = "Observation/_history";

            await ExecuteAndValidate(
                () => _client.SearchAsync(url),
                "history-type",
                ResourceType.Bundle,
                _ => url,
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAServer_WhenSearchedByHistory_ThenAuditLogEntriesShouldBeCreated()
        {
            const string url = "_history";

            await ExecuteAndValidate(
                () => _client.SearchAsync(url),
                "history-system",
                ResourceType.Bundle,
                _ => url,
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAServer_WhenSearchedByResourceInstance_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                async () =>
                {
                    FhirResponse<Observation> result = await _client.CreateAsync(Samples.GetDefaultObservation().ToPoco<Observation>());

                    return await _client.SearchAsync($"Observation/{result.Resource.Id}/_history");
                },
                "history-instance",
                ResourceType.Bundle,
                b => $"Observation/{b.Entry.First().Resource.Id}/_history",
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAServer_WhenSearchedByCompartment_ThenAuditLogEntriesShouldBeCreated()
        {
            const string url = "Patient/123/Condition";

            await ExecuteAndValidate(
                () => _client.SearchAsync(url),
                "search",
                ResourceType.Bundle,
                _ => url,
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAServer_WhenSearchedByResourceType_ThenAuditLogEntriesShouldBeCreated()
        {
            const string url = "Observation?_tag=123";

            await ExecuteAndValidate(
                () => _client.SearchAsync(url),
                "search-type",
                ResourceType.Bundle,
                _ => url,
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAServer_WhenSearchedByResourceTypeUsingPost_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                () => _client.SearchPostAsync("Observation", ("_tag", "123")),
                "search-type",
                ResourceType.Bundle,
                _ => "Observation/_search",
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAServer_WhenSearched_ThenAuditLogEntriesShouldBeCreated()
        {
            const string url = "?_tag=123";

            await ExecuteAndValidate(
                () => _client.SearchAsync(url),
                "search-system",
                ResourceType.Bundle,
                _ => url,
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenAServer_WhenSearchedUsingPost_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                () => _client.SearchPostAsync(null, ("_tag", "123")),
                "search-system",
                ResourceType.Bundle,
                _ => "_search",
                HttpStatusCode.OK);
        }

        [Fact]
        public async Task GivenARequest_WhenNoAuthorizationTokenIsSupplied_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                () =>
                {
                    FhirClient newClient = _client.Clone();

                    newClient.HttpClient.DefaultRequestHeaders.Authorization = null;

                    return newClient;
                },
                HttpStatusCode.Unauthorized,
                expectedAppId: null);
        }

        [Fact]
        public async Task GivenARequest_WhenInvalidAuthorizationTokenIsSupplied_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                () =>
                {
                    FhirClient newClient = _client.Clone();

                    newClient.HttpClient.SetBearerToken("invalid");

                    return newClient;
                },
                HttpStatusCode.Unauthorized,
                expectedAppId: null);
        }

        [Fact]
        public async Task GivenARequest_WhenValidAuthorizationTokenWithInvalidAudienceIsSupplied_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                () => _client.CreateClientForClientApplication(TestApplications.WrongAudienceClient),
                HttpStatusCode.Unauthorized,
                expectedAppId: null);
        }

        [Fact]
        public async Task GivenASmartOnFhirRequest_WhenAuthorizeIsCalled_TheAuditLogEntriesShouldBeCreated()
        {
            const string pathSegment = "AadSmartOnFhirProxy/authorize?client_id=1234&response_type=json&redirect_uri=httptest&aud=localhost";
            await ExecuteAndValidate(
                async () => await _client.HttpClient.GetAsync(pathSegment),
                "smart-on-fhir-authorize",
                pathSegment,
                HttpStatusCode.Redirect,
                "1234",
                "client_id");
        }

        [Fact]
        public async Task GivenASmartOnFhirRequest_WhenCallbackIsCalled_TheAuditLogEntriesShouldBeCreated()
        {
            const string pathSegment = "AadSmartOnFhirProxy/callback/aHR0cHM6Ly9sb2NhbGhvc3Q=?code=1234&state=1234&session_state=1234";
            await ExecuteAndValidate(
                async () => await _client.HttpClient.GetAsync(pathSegment),
                "smart-on-fhir-callback",
                pathSegment,
                HttpStatusCode.BadRequest,
                null,
                null);
        }

        [Fact]
        public async Task GivenASmartOnFhirRequest_WhenTokenIsCalled_TheAuditLogEntriesShouldBeCreated()
        {
            const string pathSegment = "AadSmartOnFhirProxy/token";
            var formFields = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("client_id", "1234"),
                new KeyValuePair<string, string>("grant_type", "grantType"),
                new KeyValuePair<string, string>("code", "code"),
                new KeyValuePair<string, string>("redirect_uri", "redirectUri"),
                new KeyValuePair<string, string>("client_secret", "client_secret"),
            };

            var content = new FormUrlEncodedContent(formFields);
            content.Headers.Add(CustomAuditHeaderPrefix + "test", "test");
            await ExecuteAndValidate(
                async () => await _client.HttpClient.PostAsync(pathSegment, content),
                "smart-on-fhir-token",
                pathSegment,
                HttpStatusCode.BadRequest,
                "1234",
                "client_id",
                new Dictionary<string, string>() { [CustomAuditHeaderPrefix + "test"] = "test" });
        }

        [Fact]
        public async Task GivenAResource_WhenNotAuthorized_ThenAuditLogEntriesShouldBeCreated()
        {
            await ExecuteAndValidate(
                () => _client.CreateClientForClientApplication(TestApplications.NativeClient),
                HttpStatusCode.Forbidden,
                expectedAppId: TestApplications.NativeClient.ClientId);
        }

        [HttpIntegrationFixtureArgumentSets(DataStore.All)]
        [Fact]
        [Trait(Traits.Priority, Priority.One)]
        [Trait(Traits.Category, Categories.Batch)]
        public async Task GivenABatch_WhenPost_ThenAuditLogEntriesShouldBeCreated()
        {
            var batch = Samples.GetDefaultBatch().ToPoco<Bundle>();

            List<(string expectedActions, string expectedPathSegments, HttpStatusCode? expectedStatusCodes, ResourceType? resourceType)> expectedList = new List<(string, string, HttpStatusCode?, ResourceType?)>
            {
                ("batch", string.Empty, HttpStatusCode.OK, ResourceType.Bundle),
                ("delete", batch.Entry[5].Request.Url, HttpStatusCode.NoContent, null),
                ("create", batch.Entry[0].Request.Url, HttpStatusCode.Created, ResourceType.Patient),
                ("create", batch.Entry[1].Request.Url, HttpStatusCode.Created, ResourceType.Patient),
                ("update", batch.Entry[2].Request.Url, HttpStatusCode.OK, ResourceType.Patient),
                ("update", batch.Entry[3].Request.Url, HttpStatusCode.Created, ResourceType.Patient),
                ("update", batch.Entry[4].Request.Url, Constants.IfMatchFailureStatus, ResourceType.OperationOutcome),
                ("search-type", batch.Entry[8].Request.Url, HttpStatusCode.OK, ResourceType.Bundle),
                ("read", batch.Entry[9].Request.Url, HttpStatusCode.NotFound, ResourceType.OperationOutcome),
            };

            await ExecuteAndValidateBatch(
               () =>
               {
                   return _client.PostBundleAsync(batch);
               },
               expectedList,
               TestApplications.ServiceClient.ClientId);
        }

        [HttpIntegrationFixtureArgumentSets(DataStore.All)]
        [Fact]
        [Trait(Traits.Priority, Priority.One)]
        [Trait(Traits.Category, Categories.Authorization)]
        [Trait(Traits.Category, Categories.Batch)]
        public async Task GivenABatchAndUserWithoutWrite_WhenPost_ThenAuditLogEntriesShouldBeCreated()
        {
            var batch = new Bundle
            {
                Type = Bundle.BundleType.Batch,
                Entry = new List<Bundle.EntryComponent>
                {
                    new Bundle.EntryComponent
                    {
                        Resource = Samples.GetDefaultObservation().ToPoco(),
                        Request = new Bundle.RequestComponent
                        {
                            Method = Bundle.HTTPVerb.POST,
                            Url = "Observation",
                        },
                    },
                    new Bundle.EntryComponent
                    {
                        Request = new Bundle.RequestComponent
                        {
                            Method = Bundle.HTTPVerb.GET,
                            Url = "Patient?name=peter",
                        },
                    },
                },
            };

            List<(string expectedActions, string expectedPathSegments, HttpStatusCode? expectedStatusCodes, ResourceType? resourceType)> expectedList = new List<(string, string, HttpStatusCode?, ResourceType?)>
            {
                ("batch", string.Empty, HttpStatusCode.OK, ResourceType.Bundle),
                ("create", "Observation", HttpStatusCode.Forbidden, ResourceType.Observation),
                ("search-type", "Patient?name=peter", HttpStatusCode.OK, ResourceType.Bundle),
            };

            FhirClient tempClient = _client.CreateClientForUser(TestUsers.ReadOnlyUser, TestApplications.NativeClient);

            await ExecuteAndValidateBatch(
                () => tempClient.PostBundleAsync(batch),
                expectedList,
                TestApplications.NativeClient.ClientId);
        }

        private async Task ExecuteAndValidate<T>(Func<Task<FhirResponse<T>>> action, string expectedAction, ResourceType expectedResourceType, Func<T, string> expectedPathGenerator, HttpStatusCode expectedStatusCode)
            where T : Resource
        {
            if (!_fixture.IsUsingInProcTestServer)
            {
                // This test only works with the in-proc server with customized middleware pipeline.
                return;
            }

            FhirResponse<T> response = await action();

            string correlationId = response.Headers.GetValues(RequestIdHeaderName).FirstOrDefault();

            Assert.NotNull(correlationId);

            var expectedUri = new Uri($"http://localhost/{expectedPathGenerator(response.Resource)}");

            string expectedAppId = TestApplications.ServiceClient.ClientId;

            Assert.Collection(
                _auditLogger.GetAuditEntriesByCorrelationId(correlationId),
                ae => ValidateExecutingAuditEntry(ae, expectedAction, expectedUri, correlationId, expectedAppId, ExpectedClaimKey),
                ae => ValidateExecutedAuditEntry(ae, expectedAction, expectedResourceType, expectedUri, expectedStatusCode, correlationId, expectedAppId, ExpectedClaimKey));
        }

        private async Task ExecuteAndValidateBatch<T>(Func<Task<FhirResponse<T>>> action, List<(string auditAction, string route, HttpStatusCode? statusCode, ResourceType? resourceType)> expectedList, string expectedAppId)
            where T : Resource
        {
            if (!_fixture.IsUsingInProcTestServer)
            {
                // This test only works with the in-proc server with customized middleware pipeline.
                return;
            }

            FhirResponse<T> response = await action();

            string correlationId = response.Headers.GetValues(RequestIdHeaderName).FirstOrDefault();
            Assert.NotNull(correlationId);

            var inspectors = new List<Action<AuditEntry>>();

            int insertIndex = 0;
            foreach ((string auditAction, string route, HttpStatusCode? statusCode, ResourceType? resourceType) in expectedList)
            {
                if (insertIndex == 2 && inspectors.Count == 2)
                {
                    insertIndex--;
                }

                if (statusCode != HttpStatusCode.Forbidden)
                {
                    inspectors.Insert(insertIndex, ae => ValidateExecutingAuditEntry(ae, auditAction, new Uri($"http://localhost/{route}"), correlationId, expectedAppId, ExpectedClaimKey));
                    insertIndex++;
                }

                inspectors.Insert(insertIndex, ae => ValidateExecutedAuditEntry(ae, auditAction, resourceType, new Uri($"http://localhost/{route}"), statusCode, correlationId, expectedAppId, ExpectedClaimKey));
                insertIndex++;
            }

            Assert.Collection(
                _auditLogger.GetAuditEntriesByCorrelationId(correlationId),
                inspectors.ToArray());
        }

        private async Task ExecuteAndValidate(Func<Task<HttpResponseMessage>> action, string expectedAction, string expectedPathSegment, HttpStatusCode expectedStatusCode, string expectedClaimValue, string expectedClaimKey, Dictionary<string, string> expectedCustomAuditHeaders = null)
        {
            if (!_fixture.IsUsingInProcTestServer)
            {
                // This test only works with the in-proc server with customized middleware pipeline
                return;
            }

            HttpResponseMessage response = await action();

            string correlationId = response.Headers.GetValues(RequestIdHeaderName).FirstOrDefault();

            Assert.NotNull(correlationId);

            var expectedUri = new Uri($"http://localhost/{expectedPathSegment}");

            Assert.Collection(
                _auditLogger.GetAuditEntriesByCorrelationId(correlationId),
                ae => ValidateExecutingAuditEntry(ae, expectedAction, expectedUri, correlationId, expectedClaimValue, expectedClaimKey, expectedCustomAuditHeaders),
                ae => ValidateExecutedAuditEntry(ae, expectedAction, null, expectedUri, expectedStatusCode, correlationId, expectedClaimValue, expectedClaimKey, expectedCustomAuditHeaders));
        }

        private async Task ExecuteAndValidate(Func<FhirClient> createClient, HttpStatusCode expectedStatusCode, string expectedAppId)
        {
            if (!_fixture.IsUsingInProcTestServer || !_fixture.FhirClient.SecuritySettings.SecurityEnabled)
            {
                // This test only works with the in-proc server with customized middleware pipeline and when security is enabled.
                return;
            }

            const string url = "Patient/123";

            // Create a new client with no token supplied.
            var client = createClient();

            FhirResponse<OperationOutcome> response = (await Assert.ThrowsAsync<FhirException>(() => client.ReadAsync<Patient>(url))).Response;

            string correlationId = response.Headers.GetValues(RequestIdHeaderName).FirstOrDefault();

            Assert.NotNull(correlationId);

            var expectedUri = new Uri($"http://localhost/{url}");

            Assert.Collection(
                _auditLogger.GetAuditEntriesByCorrelationId(correlationId),
                ae => ValidateExecutedAuditEntry(ae, "read", ResourceType.Patient, expectedUri, expectedStatusCode, correlationId, expectedAppId, ExpectedClaimKey));
        }

        private void ValidateExecutingAuditEntry(AuditEntry auditEntry, string expectedAction, Uri expectedUri, string expectedCorrelationId, string expectedClaimValue, string expectedClaimKey, Dictionary<string, string> expectedCustomAuditHeaders = null)
        {
            ValidateAuditEntry(auditEntry, AuditAction.Executing, expectedAction, null, expectedUri, null, expectedCorrelationId, expectedClaimValue, expectedClaimKey, expectedCustomAuditHeaders);
        }

        private void ValidateExecutedAuditEntry(AuditEntry auditEntry, string expectedAction, ResourceType? expectedResourceType, Uri expectedUri, HttpStatusCode? expectedStatusCode, string expectedCorrelationId, string expectedClaimValue, string expectedClaimKey, Dictionary<string, string> expectedCustomAuditHeaders = null)
        {
            ValidateAuditEntry(auditEntry, AuditAction.Executed, expectedAction, expectedResourceType, expectedUri, expectedStatusCode, expectedCorrelationId, expectedClaimValue, expectedClaimKey, expectedCustomAuditHeaders);
        }

        private void ValidateAuditEntry(AuditEntry auditEntry, AuditAction expectedAuditAction, string expectedAction, ResourceType? expectedResourceType, Uri expectedUri, HttpStatusCode? expectedStatusCode, string expectedCorrelationId, string expectedClaimValue, string expectedClaimKey, Dictionary<string, string> expectedCustomAuditHeaders = null)
        {
            Assert.NotNull(auditEntry);
            Assert.Equal(expectedAuditAction, auditEntry.AuditAction);
            Assert.Equal(expectedAction, auditEntry.Action);
            Assert.Equal(expectedResourceType?.ToString(), auditEntry.ResourceType);
            Assert.Equal(expectedUri, auditEntry.RequestUri);
            Assert.Equal(expectedStatusCode, auditEntry.StatusCode);
            Assert.Equal(expectedCorrelationId, auditEntry.CorrelationId);

            // Unfortunately, we cannot test the caller IP because these tests only run in-process, which does not go through network.

            if (expectedClaimValue != null)
            {
                Assert.Contains(
                    new KeyValuePair<string, string>(expectedClaimKey, expectedClaimValue), auditEntry.CallerClaims);
            }
            else
            {
                Assert.Empty(auditEntry.CallerClaims);
            }

            if (expectedCustomAuditHeaders != null)
            {
                Assert.Equal(expectedCustomAuditHeaders, auditEntry.CustomHeaders);
            }
            else
            {
                Assert.Empty(auditEntry.CustomHeaders);
            }
        }
    }
}