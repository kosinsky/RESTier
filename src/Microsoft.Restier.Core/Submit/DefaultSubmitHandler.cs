﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Restier.Core.Properties;

namespace Microsoft.Restier.Core.Submit
{
    /// <summary>
    /// Represents the default submit handler.
    /// </summary>
    internal static class DefaultSubmitHandler
    {
        /// <summary>
        /// Asynchronously executes the submit flow.
        /// </summary>
        /// <param name="context">
        /// The submit context.
        /// </param>
        /// <param name="cancellationToken">
        /// A cancellation token.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous
        /// operation whose result is a submit result.
        /// </returns>
        public static async Task<SubmitResult> SubmitAsync(
            SubmitContext context, CancellationToken cancellationToken)
        {
            Ensure.NotNull(context, "context");

            var preparer = context.GetApiService<IChangeSetPreparer>();
            if (preparer == null)
            {
                throw new NotSupportedException(Resources.ChangeSetPreparerMissing);
            }

            await preparer.PrepareAsync(context, cancellationToken);

            if (context.Result != null)
            {
                return context.Result;
            }

            var eventsChangeSet = context.ChangeSet;

            IEnumerable<ChangeSetEntry> currentChangeSetItems = eventsChangeSet.Entries.ToArray();

            await PerformValidate(context, currentChangeSetItems, cancellationToken);

            await PerformPreEvent(context, currentChangeSetItems, cancellationToken);

            await PerformPersist(context, currentChangeSetItems, cancellationToken);

            context.ChangeSet.Entries.Clear();

            await PerformPostEvent(context, currentChangeSetItems, cancellationToken);

            return context.Result;
        }

        private static string GetAuthorizeFailedMessage(ChangeSetEntry entry)
        {
            switch (entry.Type)
            {
                case ChangeSetEntryType.DataModification:
                    DataModificationEntry dataModification = (DataModificationEntry)entry;
                    string message = null;
                    if (dataModification.IsNew)
                    {
                        message = Resources.NoPermissionToInsertEntity;
                    }
                    else if (dataModification.IsUpdate)
                    {
                        message = Resources.NoPermissionToUpdateEntity;
                    }
                    else if (dataModification.IsDelete)
                    {
                        message = Resources.NoPermissionToDeleteEntity;
                    }
                    else
                    {
                        throw new NotSupportedException(Resources.DataModificationMustBeCUD);
                    }

                    return string.Format(CultureInfo.InvariantCulture, message, dataModification.EntitySetName);

                case ChangeSetEntryType.ActionInvocation:
                    ActionInvocationEntry actionInvocation = (ActionInvocationEntry)entry;
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        Resources.NoPermissionToInvokeAction,
                        actionInvocation.ActionName);

                default:
                    throw new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        Resources.InvalidChangeSetEntryType,
                        entry.Type));
            }
        }

        private static async Task PerformValidate(
            SubmitContext context,
            IEnumerable<ChangeSetEntry> changeSetItems,
            CancellationToken cancellationToken)
        {
            await InvokeAuthorizers(context, changeSetItems, cancellationToken);

            await InvokeValidators(context, changeSetItems, cancellationToken);

            foreach (ChangeSetEntry item in changeSetItems.Where(i => i.HasChanged()))
            {
                if (item.ChangeSetEntityState == DynamicChangeSetEntityState.ChangedWithinOwnPreEventing)
                {
                    item.ChangeSetEntityState = DynamicChangeSetEntityState.PreEvented;
                }
                else
                {
                    item.ChangeSetEntityState = DynamicChangeSetEntityState.Validated;
                }
            }
        }

        private static async Task InvokeAuthorizers(
            SubmitContext context,
            IEnumerable<ChangeSetEntry> changeSetItems,
            CancellationToken cancellationToken)
        {
            var authorizer = context.GetApiService<IChangeSetEntryAuthorizer>();
            if (authorizer == null)
            {
                return;
            }

            foreach (ChangeSetEntry entry in changeSetItems.Where(i => i.HasChanged()))
            {
                if (!await authorizer.AuthorizeAsync(context, entry, cancellationToken))
                {
                    var message = DefaultSubmitHandler.GetAuthorizeFailedMessage(entry);
                    throw new SecurityException(message);
                }
            }
        }

        private static async Task InvokeValidators(
            SubmitContext context,
            IEnumerable<ChangeSetEntry> changeSetItems,
            CancellationToken cancellationToken)
        {
            var validator = context.GetApiService<IChangeSetEntryValidator>();
            if (validator == null)
            {
                return;
            }

            ValidationResults validationResults = new ValidationResults();

            foreach (ChangeSetEntry entry in changeSetItems.Where(i => i.HasChanged()))
            {
                await validator.ValidateEntityAsync(context, entry, validationResults, cancellationToken);
            }

            if (validationResults.HasErrors)
            {
                string validationErrorMessage = Resources.ValidationFailsTheOperation;
                throw new ValidationException(validationErrorMessage)
                {
                    ValidationResults = validationResults.Errors
                };
            }
        }

        private static async Task PerformPreEvent(
            SubmitContext context,
            IEnumerable<ChangeSetEntry> changeSetItems,
            CancellationToken cancellationToken)
        {
            foreach (ChangeSetEntry entry in changeSetItems)
            {
                if (entry.ChangeSetEntityState == DynamicChangeSetEntityState.Validated)
                {
                    entry.ChangeSetEntityState = DynamicChangeSetEntityState.PreEventing;

                    var filter = context.GetApiService<IChangeSetEntryFilter>();
                    if (filter != null)
                    {
                        await filter.OnExecutingEntryAsync(context, entry, cancellationToken);
                    }

                    if (entry.ChangeSetEntityState == DynamicChangeSetEntityState.PreEventing)
                    {
                        // if the state is still the intermediate state,
                        // the entity was not changed during processing
                        // and can move to the next step
                        entry.ChangeSetEntityState = DynamicChangeSetEntityState.PreEvented;
                    }
                    else if (entry.ChangeSetEntityState == DynamicChangeSetEntityState.Changed /*&&
                        entity.Details.EntityState == originalEntityState*/)
                    {
                        entry.ChangeSetEntityState = DynamicChangeSetEntityState.ChangedWithinOwnPreEventing;
                    }
                }
            }
        }

        private static async Task PerformPersist(
            SubmitContext context,
            IEnumerable<ChangeSetEntry> changeSetItems,
            CancellationToken cancellationToken)
        {
            // Once the change is persisted, the EntityState is lost.
            // In order to invoke the correct post-CUD event, remember which action was performed on the entity.
            foreach (ChangeSetEntry item in changeSetItems)
            {
                if (item.Type == ChangeSetEntryType.DataModification)
                {
                    DataModificationEntry dataModification = (DataModificationEntry)item;
                    if (dataModification.IsNew)
                    {
                        dataModification.AddAction = AddAction.Inserting;
                    }
                    else if (dataModification.IsUpdate)
                    {
                        dataModification.AddAction = AddAction.Updating;
                    }
                    else if (dataModification.IsDelete)
                    {
                        dataModification.AddAction = AddAction.Removing;
                    }
                }
            }

            var executor = context.GetApiService<ISubmitExecutor>();
            if (executor == null)
            {
                throw new NotSupportedException(Resources.SubmitExecutorMissing);
            }

            context.Result = await executor.ExecuteSubmitAsync(context, cancellationToken);
        }

        private static async Task PerformPostEvent(
            SubmitContext context,
            IEnumerable<ChangeSetEntry> changeSetItems,
            CancellationToken cancellationToken)
        {
            foreach (ChangeSetEntry entry in changeSetItems)
            {
                var filter = context.GetApiService<IChangeSetEntryFilter>();
                if (filter != null)
                {
                    await filter.OnExecutedEntryAsync(context, entry, cancellationToken);
                }
            }
        }
    }
}
