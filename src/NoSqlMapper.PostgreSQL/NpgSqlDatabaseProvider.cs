﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using NoSqlMapper.Query;
using Npgsql;

namespace NoSqlMapper.PostgreSQL
{
    public class NpgSqlDatabaseProvider : ISqlDatabaseProvider
    {
        public string ConnectionString { get; }
        public string Host { get; private set; }
        public string Username { get; private set; }

        public NpgSqlDatabaseProvider(NsConnection connection, string connectionString)
        {
            Validate.NotNull(connection, nameof(connection));
            Validate.NotNullOrEmptyOrWhiteSpace(connectionString, nameof(connectionString));

            _connection = connection;

            Validate.NotNullOrEmptyOrWhiteSpace(connectionString, nameof(connectionString));
            ConnectionString = connectionString;
            InitializeConnectionStringParameters();
        }

        public NpgSqlDatabaseProvider(NsConnection connection, NpgsqlConnection npgSqlConnection, bool ownConnection = false)
        {
            Validate.NotNull(connection, nameof(connection));
            Validate.NotNull(npgSqlConnection, nameof(npgSqlConnection));

            _connection = connection;
            _npgSqlConnection = npgSqlConnection;
            _disposeConnection = ownConnection;
            ConnectionString = npgSqlConnection.ConnectionString;
            InitializeConnectionStringParameters();
        }

        private void InitializeConnectionStringParameters()
        {
            try
            {
                var connectionStringBuilder = new NpgsqlConnectionStringBuilder(ConnectionString);
                Host = connectionStringBuilder.Host;
                Username = connectionStringBuilder.Username;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Connection string to Sql Server seems invalid", ex);
            }

        }

        private void Log(string message)
        {
            _connection.Log?.Invoke(message);
        }

        #region Access to Sql Server

        private readonly NsConnection _connection;
        private NpgsqlConnection _npgSqlConnection;
        private readonly bool _disposeConnection = true;

        public async Task EnsureConnectionAsync()
        {
            if (_npgSqlConnection == null)
                _npgSqlConnection = new NpgsqlConnection(ConnectionString);

            if (_npgSqlConnection.State != ConnectionState.Open)
            {
                Log($"Opening connection to '{Host}'...");
                await _npgSqlConnection.OpenAsync();
                Log("Connection opened");
            }
        }

        public Task CloseConnectionAsync()
        {
            if (_npgSqlConnection != null && _npgSqlConnection.State != ConnectionState.Closed)
            {
                _npgSqlConnection?.Close();
                Log("Connection closed");
            }

            return Task.CompletedTask;
        }

        //private async Task ExecuteNonQueryAsync(params string[] sqlLines)
        //{
        //    await EnsureConnectionAsync();
        //    try
        //    {
        //        var sql = string.Join(Environment.NewLine, sqlLines);
        //        Log($"ExecuteNonQueryAsync(){Environment.NewLine}" +
        //            $"{sql}{Environment.NewLine}");
        //        using (var cmd = new NpgsqlCommand(sql, _npgSqlConnection))
        //            await cmd.ExecuteNonQueryAsync();
        //    }
        //    catch (Exception e)
        //    {
        //        Log($"ExecuteNonQueryAsync() Exception{Environment.NewLine}{e}");
        //        throw;
        //    }
        //    finally
        //    {
        //        Log($"ExecuteNonQueryAsync() Completed");
        //    }
        //}

        private async Task<object> ExecuteScalarAsync(string sql) => await ExecuteNonQueryAsync(new[] { sql }, null, executeAsScalar: true);
        private async Task<object> ExecuteScalarAsync(string sql, IDictionary<string, object> parameters) => await ExecuteNonQueryAsync(new[] { sql }, parameters: parameters, executeAsScalar: true);

        private async Task<object> ExecuteNonQueryAsync(string sql) => await ExecuteNonQueryAsync(new[] {sql}, null);
        private async Task<object> ExecuteNonQueryAsync(params string[] sqlLines) => await ExecuteNonQueryAsync(sqlLines, null);

        private async Task<object> ExecuteNonQueryAsync(string[] sqlLines, 
            IDictionary<string, object> parameters,
            bool executeAsScalar = false)
        {
            await EnsureConnectionAsync();
            try
            {
                parameters = parameters ?? new Dictionary<string, object>();

                var sql = string.Join(Environment.NewLine, sqlLines);
                Log($"ExecuteNonQueryAsync(){Environment.NewLine}" +
                    $"{sql}{Environment.NewLine}" +
                    $"{(string.Join(Environment.NewLine, parameters.Select(_ => string.Concat(_.Key, "=", _.Value))))}");

                using (var cmd = new NpgsqlCommand(sql, _npgSqlConnection))
                {
                    foreach (var paramEntry in parameters)
                        cmd.Parameters.AddWithValue(paramEntry.Key, paramEntry.Value);

                    if (executeAsScalar)
                        return await cmd.ExecuteScalarAsync();

                    await cmd.ExecuteNonQueryAsync();
                }

                return null;
            }
            catch (Exception e)
            {
                Log($"ExecuteNonQueryAsync() Exception{Environment.NewLine}{e}");
                throw;
            }
            finally
            {
                Log($"ExecuteNonQueryAsync() Completed");
            }
        }

        private async Task<IEnumerable<NsDocument>> ExecuteReaderAsync(string[] sqlLines, IDictionary<string, object> parameters)
        {
            await EnsureConnectionAsync();

            var documents = new List<NsDocument>();
            try
            {
                var sql = string.Join(Environment.NewLine, sqlLines);
                Log($"ExecuteReaderAsync(){Environment.NewLine}" +
                    $"{sql}{Environment.NewLine}" +
                    $"{(string.Join(Environment.NewLine, parameters.Select(_ => string.Concat(_.Key, "=", _.Value))))}");

                using (var cmd = new NpgsqlCommand(sql, _npgSqlConnection))
                {
                    foreach (var paramEntry in parameters)
                        cmd.Parameters.AddWithValue(paramEntry.Key, paramEntry.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            documents.Add(new NsDocument(reader["_id"], (string) reader["_document"]));
                        }
                    }

                    return documents;
                }
            }
            catch (Exception e)
            {
                Log($"ExecuteReaderAsync() Exception{Environment.NewLine}{e}");
                throw;
            }
            finally
            {
                Log($"ExecuteReaderAsync() Completed");
            }
        }
        #endregion

        #region Provider Implementation

        public async Task CreateDatabaseIfNotExistsAsync(string databaseName)
        {
            Validate.NotNullOrEmptyOrWhiteSpace(databaseName, nameof(databaseName));

            await ExecuteNonQueryAsync(
                $"CREATE SCHEMA IF NOT EXISTS \"{databaseName}\" ");
        }

        public async Task DeleteDatabaseAsync(NsDatabase database)
        {
            Validate.NotNull(database, nameof(database));

            await ExecuteNonQueryAsync($"DROP DATABASE \"{database.Name}\"");
        }

        public async Task EnsureTableAsync(NsDatabase database, string tableName, ObjectIdType objectIdType = ObjectIdType.Guid)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNullOrEmptyOrWhiteSpace(tableName, nameof(tableName));

            await ExecuteNonQueryAsync( 
                $"CREATE TABLE IF NOT EXISTS \"{database.Name}\".\"{tableName}\"(" +
                (objectIdType == ObjectIdType.Guid ? $"_id UUID" : "BIGSERIAL") + " PRIMARY KEY," +
                $"_document TEXT NOT NULL)");
        }

        public async Task EnsureIndexAsync(NsDatabase database, string tableName, TypeReflector typeReflector, string path, bool unique = false, bool ascending = true)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNullOrEmptyOrWhiteSpace(tableName, nameof(tableName));
            Validate.NotNullOrEmptyOrWhiteSpace(path, nameof(path));
            Validate.NotNull(typeReflector, nameof(typeReflector));

            /*
             USE [DatabaseTest_Index]
            IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[posts]') AND name = 'Updated')
            BEGIN
            ALTER TABLE [dbo].[Posts]
            ADD [Updated] AS (CONVERT(datetime2, JSON_VALUE([_document],'$.Updated'), 102))
            END
            IF NOT EXISTS(SELECT * FROM sys.indexes WHERE name = 'IDX_Updated' AND object_id = OBJECT_ID('posts'))
            BEGIN CREATE  NONCLUSTERED INDEX [IDX_Updated] ON [dbo].[posts]
            ( [Updated] DESC )
            WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
            END
             */

            var fieldTypes = typeReflector.Navigate(path).ToList();

            if (!fieldTypes.Any())
                throw new InvalidOperationException($"Path '{path}' doesn't represent a valid property");

            if (fieldTypes.Any(_=>_.IsArray))
                throw new InvalidOperationException($"Unable to create an index for path '{path}', it traverses arrays or collections of objects");

            var columnName = path.Replace(".", "_");
            string addColumnCommand = null;
            var lastFieldType = fieldTypes.Last();
            if (lastFieldType.Is(typeof(DateTime)))
                addColumnCommand = $"ADD [{columnName}] AS (CONVERT(datetime2, JSON_VALUE([_document],'$.{path}'), 102))";
            else if (lastFieldType.Is(typeof(int)))
                addColumnCommand = $"ADD [{columnName}] AS (CONVERT(int, JSON_VALUE([_document],'$.{path}')))";
            
            if (addColumnCommand == null)
            {
                throw new InvalidOperationException($"Type '{lastFieldType.Type}' is not (yet?) supported for indexes");    
            }

            await ExecuteNonQueryAsync( 
                $"USE [{database.Name}]",
                $"IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N\'[dbo].[{tableName}]\') AND name = \'{columnName}\')",
                $"BEGIN",
                $"ALTER TABLE [dbo].[Posts]",
                addColumnCommand,
                $"END",
                $"IF NOT EXISTS(SELECT * FROM sys.indexes WHERE name = \'IDX_{columnName}\' AND object_id = OBJECT_ID(\'{tableName}\'))",
                $"BEGIN",
                $"CREATE {(unique ? "UNIQUE" : string.Empty)} NONCLUSTERED INDEX [IDX_{columnName}] ON [dbo].[{tableName}]",
                $"( [{columnName}] {(ascending ? "ASC" : "DESC")} )",
                $"WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]",
                $"END");
        }

        public async Task DeleteIndexAsync(NsDatabase database, string tableName, string field)
        {
            Validate.NotNull(database, nameof(database));
            var columnName = field.Replace(".", "_");
            await ExecuteNonQueryAsync( 
                $"USE [{database.Name}]",
                $"IF EXISTS(SELECT * FROM sys.indexes WHERE name = \'IDX_{columnName}\' AND object_id = OBJECT_ID(\'{tableName}\'))",
                $"BEGIN",
                $"DROP INDEX [IDX_{columnName}] ON [dbo].[{tableName}]",
                $"END");
        }

        public async Task DeleteTableAsync(NsDatabase database, string tableName)
        {
            Validate.NotNull(database, nameof(database));
            await ExecuteNonQueryAsync( 
                $"DROP TABLE IF EXISTS \"{database}\".\"{tableName}\"");
        }

        public async Task<IEnumerable<NsDocument>> FindAsync(NsDatabase database, string tableName, TypeReflector typeReflector, Query.Query query = null, SortDescription[] sorts = null, int skip = 0, int take = 0)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNullOrEmptyOrWhiteSpace(tableName, nameof(tableName));

            var sql = new List<string>();
            var parameters = new List<KeyValuePair<int, object>>();

            SqlUtils.ConvertToSql(sql, typeReflector, database.Name, tableName, parameters, query, sorts);
                
            if (skip > 0)
                sql.Append($"OFFSET {skip} ROWS");

            if (take < int.MaxValue)
                sql.Append($"FETCH NEXT {take} ROWS ONLY");

            return await ExecuteReaderAsync(sql.ToArray(), parameters.ToDictionary(_ => $"@{_.Key}", _ => _.Value));
        }

        public async Task<NsDocument> FindAsync(NsDatabase database, string tableName, object id)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNullOrEmptyOrWhiteSpace(tableName, nameof(tableName));

            var sql = new List<string>();

            sql .Append($"SELECT _id, _document FROM \"{database.Name}\".\"{tableName}\"")
                .Append($"WHERE (_id = @1)");

            return (await ExecuteReaderAsync(sql.ToArray(), new Dictionary<string, object>() {{"@1", id}}))
                .FirstOrDefault();
        }

        public async Task<int> CountAsync(NsDatabase database, string tableName, TypeReflector typeReflector, Query.Query query = null)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNullOrEmptyOrWhiteSpace(tableName, nameof(tableName));

            var sql = new List<string>();
            var parameters = new List<KeyValuePair<int, object>>();

            SqlUtils.ConvertToSql(sql, typeReflector, database.Name, tableName, parameters, query, selectCount: true);

            return (int) (await ExecuteNonQueryAsync(sql.ToArray(),
                parameters.ToDictionary(_ => $"@{_.Key}", _ => _.Value), executeAsScalar: true));
        }

        public async Task<object> InsertAsync(NsDatabase database, string tableName, string json, object id, ObjectIdType typeOfObjectId)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNullOrEmptyOrWhiteSpace(tableName, nameof(tableName));
            Validate.NotNull(json, nameof(json));

            if (id != null || typeOfObjectId == ObjectIdType.Guid)
            {
                await ExecuteNonQueryAsync(new[]
                           {
                               $"INSERT INTO \"{database.Name}\".\"{tableName}\"",
                               $"           (\"_id\"",
                               $"           ,\"_document\")",
                               $"     VALUES",
                               $"           (@id",
                               $"           ,@document)"
                           },
                           new Dictionary<string, object>()
                           {
                               {"@id", id ?? Guid.NewGuid()},
                               {"@document", json}
                           },
                           executeAsScalar: false);

                return id;
            }
            
            return await ExecuteNonQueryAsync(new[]
                       {
                           $"INSERT INTO \"{database.Name}\".\"{tableName}\"",
                           $"           (\"_document\")" +
                           $"     VALUES",
                           $"           (@document)",
                           $"RETURNING _id"
                       },
                       new Dictionary<string, object>()
                       {
                           {"@document", json}
                       },
                       executeAsScalar: true);
        }

        public Task UpdateAsync(NsDatabase database, string tableName, string json, object id)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNullOrEmptyOrWhiteSpace(tableName, nameof(tableName));
            Validate.NotNull(json, nameof(json));
            Validate.NotNull(id, nameof(id));

            throw new NotImplementedException();
        }

        public Task UpsertAsync(NsDatabase database, string tableName, string json, object id)
        {
            Validate.NotNull(database, nameof(database));
            Validate.NotNull(json, nameof(json));
            Validate.NotNull(id, nameof(id));

            throw new NotImplementedException();
        }

        public Task DeleteAsync(NsDatabase database, string tableName, object id)
        {
            throw new NotImplementedException();
        }

        #endregion


        #region IDisposable Support
        private bool _disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    if (_disposeConnection)
                        _npgSqlConnection?.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                _disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~NsConnection() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion

    }
}
