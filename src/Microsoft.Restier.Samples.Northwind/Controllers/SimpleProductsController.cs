using Microsoft.Restier.Samples.Northwind.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using System.Web.OData;

namespace Microsoft.Restier.Samples.Northwind.Controllers
{
    public class SimpleProductsController : ODataController
    {
        private NorthwindContext DbContext = new NorthwindContext();

        private List<Product> products = new List<Product>()
        {
            new Product()
            {
                ProductID = 1,
                ProductName = "Bread",
              
            },
        };

        [EnableQuery]
        public IQueryable<Product> Get()
        {
            return DbContext.Products;
            //return products.AsQueryable();
        }
    }
}
