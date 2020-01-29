﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.SqlServer.Api.Controllers;
using Microsoft.Health.Fhir.SqlServer.Api.Features.Filters;
using Microsoft.Health.Fhir.Tests.Common;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.Api.UnitTests.Features.Filters
{
    public class HttpExceptionFilterTests
    {
        private readonly ActionExecutedContext _context;

        public HttpExceptionFilterTests()
        {
            _context = new ActionExecutedContext(
                new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
                new List<IFilterMetadata>(),
                Mock.TypeWithArguments<SchemaController>(NullLogger<SchemaController>.Instance));
        }

        [Fact]
        public void GivenANotImplementedException_WhenExecutingAnAction_ThenTheResponseShouldBeAJsonResultWithNotImplementedStatusCode()
        {
            var filter = new HttpExceptionFilterAttribute();

            _context.Exception = Substitute.For<NotImplementedException>();

            filter.OnActionExecuted(_context);

            var result = _context.Result as JsonResult;

            Assert.NotNull(result);
            Assert.Equal((int)HttpStatusCode.NotImplemented, result.StatusCode);
        }

        [Fact]
        public void GivenANotFoundException_WhenExecutingAnAction_ThenTheResponseShouldBeAJsonResultWithNotFoundStatusCode()
        {
            var filter = new HttpExceptionFilterAttribute();

            _context.Exception = Substitute.For<FileNotFoundException>();

            filter.OnActionExecuted(_context);

            var result = _context.Result as JsonResult;

            Assert.NotNull(result);
            Assert.Equal((int)HttpStatusCode.NotFound, result.StatusCode);
        }
    }
}
