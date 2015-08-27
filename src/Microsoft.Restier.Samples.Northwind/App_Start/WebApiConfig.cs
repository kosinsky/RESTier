// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System.Web.Http;
using System.Web.OData.Extensions;
using Microsoft.Restier.Samples.Northwind.Controllers;
using Microsoft.Restier.WebApi;
using Microsoft.Restier.WebApi.Batch;
using System.Web.OData.Builder;
using Microsoft.Restier.Samples.Northwind.Models;

namespace Microsoft.Restier.Samples.Northwind
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Web API configuration and services

            // Web API routes
            config.MapHttpAttributeRoutes();

            config.EnableUnqualifiedNameCall(true);
            RegisterNorthwind(config, GlobalConfiguration.DefaultServer);


            var builder = new ODataConventionModelBuilder();

            builder.EntitySet<Product>("SimpleProducts");

            config.MapODataServiceRoute("ODataRoute", null, builder.GetEdmModel());

        }

        public static async void RegisterNorthwind(
            HttpConfiguration config, HttpServer server)
        {
            await config.MapODataDomainRoute<NorthwindController>(
                "NorthwindApi", "api/Northwind",
                new ODataDomainBatchHandler(server));
        }
    }
}
