namespace Sc.Modules.Events.LayoutDataSourceReferenceUpdater
{
    using System;
    using System.Linq;
    using Sitecore;
    using Sitecore.Collections;
    using Sitecore.Data;
    using Sitecore.Data.Fields;
    using Sitecore.Data.Items;
    using Sitecore.Diagnostics;
    using Sitecore.Events;
    using Sitecore.Globalization;
    using Sitecore.Layouts;
    using Sitecore.Shell.Applications.Dialogs.ProgressBoxes;

    /// <summary>
    ///     Class used to update datasources in Shared Layout and Final Layout field, when copying trees in Sitecore.
    /// </summary>
    public class LayoutDataSourceReferenceUpdater
    {
        #region Properties

        /// <summary>
        ///     Gets or sets the new root Item (source item).
        /// </summary>
        /// <value>The new root Item.</value>
        public Item NewRoot { get; set; }

        /// <summary>
        ///     Gets or sets the original root Item (destination item).
        /// </summary>
        /// <value>The original root Item.</value>
        public Item OriginalRoot { get; set; }

        #endregion Properties

        #region Methods

        /// <summary>
        ///     Updates the references using <see cref="OriginalRoot" /> and <see cref="NewRoot" />.
        /// </summary>
        /// <param name="parameters">The parameters.</param>
        public void UpdateReferences(object[] parameters)
        {
            Assert.IsNotNull(OriginalRoot, "OriginalRoot cannot be null");
            Assert.IsNotNull(NewRoot, "NewRoot cannot be null");

            UpdateTree(OriginalRoot);
        }

        /// <summary>
        ///     Finds the corresponding item using <see cref="OriginalRoot" /> and <see cref="NewRoot" />.
        /// </summary>
        /// <param name="itemBeingCopied">The item being copied.</param>
        /// <returns></returns>
        private Item FindCorrespondingItem(Item itemBeingCopied)
        {
            if (!itemBeingCopied.Axes.IsDescendantOf(OriginalRoot))
            {
                return null;
            }

            var relativePath = itemBeingCopied.Paths.FullPath.Substring(OriginalRoot.Paths.FullPath.Length);
            var newItemPath = string.Concat(NewRoot.Paths.FullPath, relativePath);

            return NewRoot.Database.GetItem(newItemPath, itemBeingCopied.Language, itemBeingCopied.Version);
        }

        /// <summary>
        ///     Updates the items Context fields to point to correct references.
        /// </summary>
        /// <param name="itemBeingCopied">The item being copied.</param>
        private void UpdateItemFields(Item itemBeingCopied)
        {
            var layoutField = new LayoutField(itemBeingCopied.Fields[FieldIDs.LayoutField]);
            if (layoutField.InnerField.GetValue(true, true) != null && itemBeingCopied.Languages.Any())
            {
                var newItem = FindCorrespondingItem(itemBeingCopied);
                if (newItem == null)
                {
                    Log.Warn(
                        string.Format("Unable to find corresponding item for copied item {0} - root {1}.",
                            itemBeingCopied.Paths.FullPath, OriginalRoot.Paths.FullPath), this);
                    return;
                }

                var sharedLayoutUpdated = false;

                foreach (var language in itemBeingCopied.Languages)
                {
                    var versionedItem = itemBeingCopied.Database.GetItem(itemBeingCopied.ID, language,
                        itemBeingCopied.Version);
                    if (versionedItem != null && versionedItem.Versions.Count > 0)
                    {
                        foreach (var itemVersion in versionedItem.Versions.GetVersions())
                        {
                            if (!sharedLayoutUpdated)
                            {
                                newItem = UpdateItemLayouts(itemBeingCopied, itemVersion, newItem, FieldIDs.LayoutField);
                                sharedLayoutUpdated = true;
                            }

                            newItem = UpdateItemLayouts(itemBeingCopied, itemVersion, newItem, FieldIDs.FinalLayoutField);
                        }
                    }
                }
            }
        }

        private Item UpdateItemLayouts(Item itemBeingCopied, Item itemVersion, Item newItem, ID fieldId)
        {
            if (itemVersion != null)
            {
                // Loop all datasources, if sources is child tree FindCorrespondingItem and replace
                var layoutField = new LayoutField(itemVersion.Fields[fieldId]);
                if (layoutField.InnerField.GetValue(true, true) == null)
                {
                    return newItem;
                }

                var rawLayout = itemVersion.Fields[fieldId].Value;
                if (string.IsNullOrWhiteSpace(layoutField.Value))
                {
                    return newItem;
                }
                var layout = LayoutDefinition.Parse(layoutField.Value);

                foreach (DeviceDefinition device in layout.Devices)
                {
                    foreach (RenderingDefinition rendering in device.Renderings)
                    {
                        if (!string.IsNullOrEmpty(rendering.Datasource))
                        {
                            var datasourceItem = itemVersion.Database.GetItem(rendering.Datasource);
                            if (datasourceItem == null)
                            {
                                Log.Warn(
                                    string.Format("Could not find datasource item {0} while copying item {1}",
                                        rendering.Datasource, itemVersion.ID), this);
                                continue;
                            }

                            // Exit if datasource is not a descendend of the item being copied
                            if (!datasourceItem.Axes.IsDescendantOf(itemBeingCopied))
                            {
                                continue;
                            }

                            // Find new datasource if it is not under path of parent
                            var correspondingItem = FindCorrespondingItem(datasourceItem);
                            if (correspondingItem == null)
                            {
                                Log.Warn(
                                    string.Format("Could not find datasource target item {0} while copying item {1}",
                                        rendering.Datasource, itemVersion.ID), this);
                                continue;
                            }

                            rawLayout = rawLayout.Replace(datasourceItem.ID.ToString(),
                                correspondingItem.ID.ToString());
                        }
                    }
                }
                // Replace Layout field on new item 
                newItem = itemVersion.Database.GetItem(newItem.ID, itemVersion.Language,
                    itemVersion.Version);

                if (rawLayout != newItem.Fields[fieldId].Value)
                {
                    // Save updated layout information
                    newItem.Editing.BeginEdit();
                    newItem.Fields[fieldId].Value = rawLayout;
                    newItem.Editing.EndEdit();
                }
            }
            return newItem;
        }

        /// <summary>
        ///     Updates a tree in Sitecore (fixes all references in the tree).
        /// </summary>
        /// <param name="root">The root Item.</param>
        private void UpdateTree(Item root)
        {
            UpdateItemFields(root);
            foreach (Item child in root.GetChildren(ChildListOptions.None))
            {
                UpdateTree(child);
            }
        }


        /// <summary>
        ///     Called when the item copied event runs. Will update the Context references contained in the copied tree structure.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="args">The <see cref="System.EventArgs" /> instance containing the event data.</param>
        protected void OnItemCopied(object sender, EventArgs args)
        {
            var itemThatWasCopied = Event.ExtractParameter(args, 0) as Item;
            var itemRootCreatedByCopy = Event.ExtractParameter(args, 1) as Item;
            Error.AssertNotNull(itemThatWasCopied, "No sourceItem in parameters");
            Error.AssertNotNull(itemRootCreatedByCopy, "No targetItem in parameters");

            var updater = new LayoutDataSourceReferenceUpdater
            {
                NewRoot = itemRootCreatedByCopy,
                OriginalRoot = itemThatWasCopied
            };

            ProgressBox.Execute(
                Translate.Text("Updating context references"),
                Translate.Text("Updating context references"),
                "Applications/16x16/form_blue.png",
                updater.UpdateReferences, Event.ExtractParameter(args, 0), Event.ExtractParameter(args, 1));
        }

        #endregion Methods
    }
}
