﻿using System;
using System.Net;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Xml.Linq;
using NWebDav.Server.Helpers;
using NWebDav.Server.Http;
using NWebDav.Server.Stores;

namespace NWebDav.Server.Handlers
{
    [Verb("COPY")]
    public class CopyHandler : IRequestHandler
    {
        public async Task<bool> HandleRequestAsync(IHttpContext httpContext, IStore store)
        {
            // Obtain request and response
            var request = httpContext.Request;
            var response = httpContext.Response;
            var principal = httpContext.Session?.Principal;

            // Obtain the destination
            var destinationUri = request.GetDestinationUri();
            if (destinationUri == null)
            {
                // Bad request
                response.SendResponse(DavStatusCode.BadRequest, "Destination header is missing.");
                return true;
            }

            // Make sure the source and destination are different
            if (request.Url.AbsoluteUri.Equals(destinationUri.AbsoluteUri, StringComparison.CurrentCultureIgnoreCase))
            {
                // Forbidden
                response.SendResponse(DavStatusCode.Forbidden, "Source and destination cannot be the same.");
                return true;
            }

            // Check if the Overwrite header is set
            var overwrite = request.GetOverwrite();

            // Split the destination Uri
            var destination = RequestHelper.SplitUri(destinationUri);

            // Obtain the destination collection
            var destinationCollection = await store.GetCollectionAsync(destination.CollectionUri, principal).ConfigureAwait(false);
            if (destinationCollection == null)
            {
                // Source not found
                response.SendResponse(DavStatusCode.Conflict, "Destination cannot be found or is not a collection.");
                return true;
            }

            // Obtain the source item
            var sourceItem = await store.GetItemAsync(request.Url, principal).ConfigureAwait(false);
            if (sourceItem == null)
            {
                // Source not found
                response.SendResponse(DavStatusCode.NotFound, "Source cannot be found.");
                return true;
            }

            // Determine depth
            var depth = request.GetDepth();

            // Keep track of all errors
            var errors = new UriResultCollection();

            // Copy collection
            await CopyAsync(sourceItem, destinationCollection, destination.Name, overwrite, depth, principal, destination.CollectionUri, errors).ConfigureAwait(false);

            // Check if there are any errors
            if (errors.HasItems)
            {
                // Obtain the status document
                var xDocument = new XDocument(errors.GetXmlMultiStatus());

                // Stream the document
                await response.SendResponseAsync(DavStatusCode.MultiStatus, xDocument).ConfigureAwait(false);
            }
            else
            {
                // Set the response
                response.SendResponse(DavStatusCode.OK);
            }

            return true;
        }

        private async Task CopyAsync(IStoreItem source, IStoreCollection destinationCollection, string name, bool overwrite, int depth, IPrincipal principal, Uri baseUri, UriResultCollection errors)
        {
            // Determine the new base Uri
            var newBaseUri = new Uri(baseUri, name);

            // Copy the item
            var copyResult = await source.CopyAsync(destinationCollection, name, overwrite, principal).ConfigureAwait(false);
            if (copyResult.Result != DavStatusCode.Created && copyResult.Result != DavStatusCode.NoContent)
            {
                errors.AddResult(newBaseUri, copyResult.Result);
                return;
            }

            // Check if the source is a collection and we are requested to copy recursively
            var sourceCollection = source as IStoreCollection;
            if (sourceCollection != null && depth > 0)
            {
                // The result should also contain a collection
                var newCollection = (IStoreCollection)copyResult.Item;

                // Copy all childs of the source collection
                foreach (var entry in await sourceCollection.GetItemsAsync(principal).ConfigureAwait(false))
                    await CopyAsync(entry, newCollection, entry.Name, overwrite, depth - 1, principal, newBaseUri, errors).ConfigureAwait(false);
            }
        }
    }
}
