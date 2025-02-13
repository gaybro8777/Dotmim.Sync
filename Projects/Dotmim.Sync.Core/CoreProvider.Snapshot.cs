﻿using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Manager;
using Dotmim.Sync.Serialization;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    public abstract partial class CoreProvider
    {
        /// <summary>
        /// update configuration object with tables desc from server database
        /// </summary>
        public virtual async Task<SyncContext> CreateSnapshotAsync(SyncContext context, SyncSet schema,
                             DbConnection connection, DbTransaction transaction, string batchDirectory, int batchSize, long remoteClientTimestamp,
                             CancellationToken cancellationToken, IProgress<ProgressArgs> progress = null)
        {

            // create local directory
            if (!Directory.Exists(batchDirectory))
                Directory.CreateDirectory(batchDirectory);

            // numbers of batch files generated
            var batchIndex = 0;

            // create the in memory changes set
            var changesSet = new SyncSet();

            // Create a Schema set without readonly tables, attached to memory changes
            foreach (var table in schema.Tables)
                DbSyncAdapter.CreateChangesTable(schema.Tables[table.TableName, table.SchemaName], changesSet);

            var sb = new StringBuilder();
            var underscore = "";

            if (context.Parameters != null)
            {
                foreach (var p in context.Parameters.OrderBy(p =>p.Name))
                {
                    var cleanValue = new string(p.Value.ToString().Where(char.IsLetterOrDigit).ToArray());
                    var cleanName = new string(p.Name.Where(char.IsLetterOrDigit).ToArray());

                    sb.Append($"{underscore}{cleanName}_{cleanValue}");
                    underscore = "_";
                }
            }

            var directoryName = sb.ToString();
            directoryName = string.IsNullOrEmpty(directoryName) ? "ALL" : directoryName;

            var directoryFullPath = Path.Combine(batchDirectory, directoryName);

            if (Directory.Exists(directoryFullPath))
                Directory.Delete(directoryFullPath, true);

            // batchinfo generate a schema clone with scope columns if needed
            var batchInfo = new BatchInfo(false, changesSet, batchDirectory, directoryName);

            // Clear tables, we will add only the ones we need in the batch info
            changesSet.Clear();

            foreach (var syncTable in schema.Tables)
            {
                var tableBuilder = this.GetTableBuilder(syncTable);
                var syncAdapter = tableBuilder.CreateSyncAdapter(connection, transaction);

                // raise before event
                context.SyncStage = SyncStage.TableChangesSelecting;
                var tableChangesSelectingArgs = new TableChangesSelectingArgs(context, syncTable.TableName, connection, transaction);
                // launch interceptor if any
                await this.InterceptAsync(tableChangesSelectingArgs).ConfigureAwait(false);

                // Get Select initialize changes command
                var selectIncrementalChangesCommand = this.GetSelectChangesCommand(context, syncAdapter, syncTable, true);

                // Set parameters
                this.SetSelectChangesCommonParameters(context, syncTable, null, true, 0, selectIncrementalChangesCommand);

                // Get the reader
                using (var dataReader = selectIncrementalChangesCommand.ExecuteReader())
                {
                    // memory size total
                    double rowsMemorySize = 0L;

                    // Create a chnages table with scope columns
                    var changesSetTable = DbSyncAdapter.CreateChangesTable(schema.Tables[syncTable.TableName, syncTable.SchemaName], changesSet);

                    while (dataReader.Read())
                    {
                        // Create a row from dataReader
                        var row = CreateSyncRowFromReader(dataReader, changesSetTable);

                        // Add the row to the changes set
                        changesSetTable.Rows.Add(row);

                        var fieldsSize = ContainerTable.GetRowSizeFromDataRow(row.ToArray());
                        var finalFieldSize = fieldsSize / 1024d;

                        if (finalFieldSize > batchSize)
                            throw new RowOverSizedException(finalFieldSize.ToString());

                        // Calculate the new memory size
                        rowsMemorySize += finalFieldSize;

                        // Next line if we don't reach the batch size yet.
                        if (rowsMemorySize <= batchSize)
                            continue;

                        // add changes to batchinfo
                        batchInfo.AddChanges(changesSet, batchIndex, false);

                        // increment batch index
                        batchIndex++;

                        // we know the datas are serialized here, so we can flush  the set
                        changesSet.Clear();

                        // Recreate an empty ContainerSet and a ContainerTable
                        changesSet = new SyncSet();

                        changesSetTable = DbSyncAdapter.CreateChangesTable(schema.Tables[syncTable.TableName, syncTable.SchemaName], changesSet);

                        // Init the row memory size
                        rowsMemorySize = 0L;
                    }
                }

                selectIncrementalChangesCommand.Dispose();
            }


            if (changesSet != null && changesSet.HasTables)
                batchInfo.AddChanges(changesSet, batchIndex, true);

            // Check the last index as the last batch
            batchInfo.EnsureLastBatch();

            batchInfo.Timestamp = remoteClientTimestamp;

            // Serialize on disk.
            var jsonConverter = new JsonConverter<BatchInfo>();

            var summaryFileName = Path.Combine(directoryFullPath, "summary.json");

            using (var f = new FileStream(summaryFileName, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var bytes = jsonConverter.Serialize(batchInfo);
                f.Write(bytes, 0, bytes.Length);
            }


            return context;
        }


        /// <summary>
        /// Gets a batch of changes to synchronize when given batch size, 
        /// destination knowledge, and change data retriever parameters.
        /// </summary>
        /// <returns>A DbSyncContext object that will be used to retrieve the modified data.</returns>
        public virtual (SyncContext, BatchInfo) GetSnapshot(
                             SyncContext context, SyncSet schema, string batchDirectory,
                             CancellationToken cancellationToken, IProgress<ProgressArgs> progress = null)
        {

            var sb = new StringBuilder();
            var underscore = "";

            if (context.Parameters != null)
            {
                foreach (var p in context.Parameters.OrderBy(p => p.Name))
                {
                    var cleanValue = new string(p.Value.ToString().Where(char.IsLetterOrDigit).ToArray());
                    var cleanName = new string(p.Name.Where(char.IsLetterOrDigit).ToArray());

                    sb.Append($"{underscore}{cleanName}_{cleanValue}");
                    underscore = "_";
                }
            }

            var directoryName = sb.ToString();
            directoryName = string.IsNullOrEmpty(directoryName) ? "ALL" : directoryName;

            var directoryFullPath = Path.Combine(batchDirectory, directoryName);

            // if no snapshot present, just return null value.
            if (!Directory.Exists(directoryFullPath))
                return (context, null);

            // Serialize on disk.
            var jsonConverter = new JsonConverter<BatchInfo>();

            var summaryFileName = Path.Combine(directoryFullPath, "summary.json");

            BatchInfo batchInfo = null;

            // Create the schema changeset
            var changesSet = new SyncSet(schema.ScopeName);

            // Create a Schema set without readonly columns, attached to memory changes
            foreach (var table in schema.Tables)
                DbSyncAdapter.CreateChangesTable(schema.Tables[table.TableName, table.SchemaName], changesSet);


            using (var fs = new FileStream(summaryFileName, FileMode.Open, FileAccess.Read))
            {
                batchInfo = jsonConverter.Deserialize(fs);
            }

            batchInfo.SetSchema(changesSet);

            return (context, batchInfo);

        }


    }
}
