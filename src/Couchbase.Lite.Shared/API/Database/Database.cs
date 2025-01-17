﻿// 
//  Database.cs
// 
//  Copyright (c) 2017 Couchbase, Inc All rights reserved.
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//  http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
// 

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Couchbase.Lite.DI;
using Couchbase.Lite.Internal.Doc;
using Couchbase.Lite.Internal.Logging;
using Couchbase.Lite.Internal.Query;
using Couchbase.Lite.Internal.Serialization;
using Couchbase.Lite.Logging;
using Couchbase.Lite.Query;
using Couchbase.Lite.Support;
using Couchbase.Lite.Sync;
using Couchbase.Lite.Util;

using LiteCore;
using LiteCore.Interop;
using LiteCore.Util;

using Newtonsoft.Json;

using NotNull = JetBrains.Annotations.NotNullAttribute;
using ItemNotNull = JetBrains.Annotations.ItemNotNullAttribute;
using CanBeNull = JetBrains.Annotations.CanBeNullAttribute;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
#if COUCHBASE_ENTERPRISE
using Couchbase.Lite.P2P;
#endif

namespace Couchbase.Lite
{

    /// <summary>
    /// Specifies the way that the library should behave when it encounters a situation
    /// when the database has been altered since the last read (e.g. a local operation read
    /// a document, modified it, and while it was being modified a replication committed a
    /// change to the document, and then the local document was saved after that)
    /// </summary>
    public enum ConcurrencyControl
    {
        /// <summary>
        /// Disregard the version that was received out of band and
        /// force this version to be current
        /// </summary>
        LastWriteWins,

        /// <summary>
        /// Throw an exception to indicate the situation so that the latest
        /// data can be read again from the local database
        /// </summary>
        FailOnConflict
    }

    /// <summary>
    /// Maintenance Type used when performing database maintenance .
    /// </summary>
    public enum MaintenanceType
    {
        /// <summary>
        /// Compact the database file and delete unused attachments.
        /// </summary>
        Compact,

        /// <summary>
        /// [VOLATILE] Rebuild the entire database's indexes.
        /// </summary>
        Reindex,

        /// <summary>
        /// [VOLATILE] Check for the database’s corruption. If found, an error will be returned.
        /// </summary>
        IntegrityCheck,

        /// <summary>
        /// Quickly update db statistics to help optimize queries
        /// </summary>
        Optimize,

        /// <summary>
        /// Full update of db statistics; takes longer than Optimize
        /// </summary>
        FullOptimize
    }

    /// <summary>
    /// A Couchbase Lite database.  This class is responsible for CRUD operations revolving around
    /// <see cref="Document"/> instances.  It is portable between platforms if the file is retrieved,
    /// and can be seeded with pre-populated data if desired.
    /// </summary>
    public sealed unsafe partial class Database : IDisposable
    {
        #region Constants

        private static readonly C4DatabaseConfig2 DBConfig = new C4DatabaseConfig2 {
            flags = C4DatabaseFlags.Create | C4DatabaseFlags.AutoCompact,
        };

        private const string DBExtension = "cblite2";

        private const string Tag = nameof(Database);

        private static readonly C4DocumentObserverCallback _DocumentObserverCallback = DocObserverCallback;
        private static readonly C4DatabaseObserverCallback _DatabaseObserverCallback = DbObserverCallback;

        #endregion

        #region Variables

        [@NotNull]
        private readonly Dictionary<string, Tuple<IntPtr, GCHandle>> _docObs = new Dictionary<string, Tuple<IntPtr, GCHandle>>();

        [@NotNull]
        private readonly FilteredEvent<string, DocumentChangedEventArgs> _documentChanged =
            new FilteredEvent<string, DocumentChangedEventArgs>();

        [@NotNull]
        private readonly Event<DatabaseChangedEventArgs> _databaseChanged = 
            new Event<DatabaseChangedEventArgs>();

        [@NotNull]
        private readonly HashSet<Document> _unsavedDocuments = new HashSet<Document>();

        [@NotNull]
        private readonly TaskFactory _callbackFactory = new TaskFactory(new QueueTaskScheduler());

#if false
        private IJsonSerializer _jsonSerializer;
#endif

        private C4DatabaseObserver* _obs;
        private GCHandle _obsContext;
        private C4Database* _c4db;
        private bool _isClosing;
        private ManualResetEventSlim _closeCondition = new ManualResetEventSlim(true);

        #endregion

        #region Properties

        /// <summary>
        /// Gets the configuration that was used to create the database.  The returned object
        /// is readonly; an <see cref="InvalidOperationException"/> will be thrown if the configuration
        /// object is modified.
        /// </summary>
        [@NotNull]
        public DatabaseConfiguration Config { get; }

        /// <summary>
        /// Gets the number of documents in the database
        /// </summary>
        public ulong Count => ThreadSafety.DoLocked(() => Native.c4db_getDocumentCount(_c4db));

        /// <summary>
        /// Gets a <see cref="DocumentFragment"/> with the given document ID
        /// </summary>
        /// <param name="id">The ID of the <see cref="DocumentFragment"/> to retrieve</param>
        /// <returns>The <see cref="DocumentFragment"/> object</returns>
        [@NotNull]
        public DocumentFragment this[string id] => new DocumentFragment(GetDocument(id));

        /// <summary>
        /// Gets the object that stores the available logging methods
        /// for Couchbase Lite
        /// </summary>
        [@NotNull]
        public static Log Log { get; } = new Log();

        /// <summary>
        /// Gets the database's name
        /// </summary>
        [@NotNull]
        public string Name { get; }

        /// <summary>
        /// Gets the database's path.  If the database is closed or deleted, a <c>null</c>
        /// value will be returned.
        /// </summary>
        [@CanBeNull]
        public string Path
        {
            get {
                return ThreadSafety.DoLocked(() => _c4db != null ? Native.c4db_getPath(c4db) : null);
            }
        }

        internal ConcurrentDictionary<IStoppable, int> ActiveStoppables { get; } = new ConcurrentDictionary<IStoppable, int>();


        internal FLSliceResult PublicUUID
        {
            get {
                var retVal = new FLSliceResult(null, 0UL);
                ThreadSafety.DoLocked(() =>
                {
                    CheckOpen();
                    var publicUUID = new C4UUID();
                    C4Error err;
                    var uuidSuccess = Native.c4db_getUUIDs(_c4db, &publicUUID, null, &err);
                    if (!uuidSuccess) {
                        throw CouchbaseException.Create(err);
                    }
                    
                    retVal = Native.FLSlice_Copy(new FLSlice(publicUUID.bytes, (ulong) C4UUID.Size));
                });

                return retVal;
            }
        }

        internal C4BlobStore* BlobStore
        {
            get {
                C4BlobStore* retVal = null;
                ThreadSafety.DoLocked(() =>
                {
                    CheckOpen();
                    retVal = (C4BlobStore*) LiteCoreBridge.Check(err => Native.c4db_getBlobStore(c4db, err));
                });

                return retVal;
            }
        }

        internal C4Database* c4db
        {
            get {
                C4Database* retVal = null;
                ThreadSafety.DoLocked(() => retVal = _c4db);
                return retVal;
            }
        }

        internal FLEncoder* SharedEncoder
        {
            get {
                FLEncoder* encoder = null;
                ThreadSafety.DoLocked(() =>
                {
                    CheckOpen();
                    encoder = Native.c4db_getSharedFleeceEncoder(_c4db);
                });

                return encoder;
            }
        }

        [@NotNull]
        internal ThreadSafety ThreadSafety { get; } = new ThreadSafety();

        internal bool IsClosedLocked
        {
            get {
                return ThreadSafety.DoLocked(() =>
                {
                    return IsClosed;
                });
            }
        }

        private bool IsShell { get; } //this object is borrowing the C4Database from somewhere else, so don't free C4Database at the end if isshell

        // Must be called inside self lock
        private bool IsClosed
        {
            get {
                return _c4db == null;
            }
        }

        private bool IsReadyToClose
        {
            get {
                return ThreadSafety.DoLocked(() =>
                {
                    return ActiveStoppables.Count == 0;
                });
            }
        }

        #endregion

        #region Constructors

        static Database()
        {
            Native.c4log_enableFatalExceptionBacktrace();
        }

        /// <summary>
        /// Creates a database with a given name and database configuration.  If the configuration
        /// is <c>null</c> then the default configuration will be used.  If the database does not yet
        /// exist, it will be created.
        /// </summary>
        /// <param name="name">The name of the database</param>
        /// <param name="configuration">The database configuration, or <c>null</c> for the default configuration</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is <c>null</c></exception>
        /// <exception cref="CouchbaseLiteException">Thrown with <see cref="CouchbaseLiteError.CantOpenFile"/> if the
        /// directory indicated in <paramref name="configuration"/> could not be created</exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition was returned by LiteCore</exception>
        public Database([@NotNull]string name, [@CanBeNull]DatabaseConfiguration configuration = null)
        {
            Name = CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(name), name);
            if(name == "") {
                var err = new C4Error(C4ErrorDomain.LiteCoreDomain, (int) CouchbaseLiteError.WrongFormat);
                throw new CouchbaseLiteException(err);
            }

            Config = configuration?.Freeze() ?? new DatabaseConfiguration(true);
            Run.Once(nameof(CheckFileLogger), CheckFileLogger);
            Open();
        }

        private void CheckFileLogger()
        {
            if (Log.File.Config == null) {
                WriteLog.To.Database.W("Logging", "Database.Log.File.Config is null, meaning file logging is disabled.  Log files required for product support are not being generated.");
            }
        }

        internal Database([@NotNull]Database other)
            : this(other.Name, other.Config)
        {

        }

        #if !COUCHBASE_ENTERPRISE
        [ExcludeFromCodeCoverage]
        #endif
        // Used for predictive query callback
        internal Database(C4Database* c4db)
        {
            Name = "tmp";
            Config = new DatabaseConfiguration(true);
            _c4db = (C4Database*) Native.c4db_retain(c4db);
            IsShell = true;
        }

        /// <summary>
        /// Finalizer
        /// </summary>
        ~Database()
        {
            try {
                Dispose(false);
            } catch (Exception e) {
                WriteLog.To.Database.E(Tag, "Error during finalizer, swallowing!", e);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Copies a canned database from the given path to a new database with the given name and
        /// the configuration.  The new database will be created at the directory specified in the
        /// configuration.  Without given the database configuration, the default configuration that
        /// is equivalent to setting all properties in the configuration to <c>null</c> will be used.
        /// </summary>
        /// <param name="path">The source database path (i.e. path to the cblite2 folder)</param>
        /// <param name="name">The name of the new database to be created</param>
        /// <param name="config">The database configuration for the new database</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="path"/> or <paramref name="name"/>
        /// are <c>null</c></exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        public static void Copy([@NotNull]string path, [@NotNull]string name, [@CanBeNull]DatabaseConfiguration config)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(path), path);
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(name), name);

            LiteCoreBridge.Check(err =>
            {
                var nativeConfig = DBConfig;
                nativeConfig.ParentDirectory = config?.Directory;

                #if COUCHBASE_ENTERPRISE
                if (config?.EncryptionKey != null) {
                    var key = config.EncryptionKey;
                    var i = 0;
                    nativeConfig.encryptionKey.algorithm = C4EncryptionAlgorithm.AES256;
                    foreach (var b in key.KeyData) {
                        nativeConfig.encryptionKey.bytes[i++] = b;
                    }
                }
                #endif

                return Native.c4db_copyNamed(path, name, &nativeConfig, err);
            });

        }

        /// <summary>
        /// Deletes a database of the given name in the given directory.  If a <c>null</c> directory
        /// is passed then the default directory is searched.
        /// </summary>
        /// <param name="name">The database name</param>
        /// <param name="directory">The directory where the database is located, or <c>null</c> to check the default directory</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is <c>null</c></exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        public static void Delete([@NotNull]string name, [@CanBeNull]string directory)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(name), name);

            var path = DatabasePath(directory);
            LiteCoreBridge.Check(err => Native.c4db_deleteNamed(name, path, err) || err->code == 0);
        }

        /// <summary>
        /// Checks whether a database of the given name exists in the given directory or not.  If a
        /// <c>null</c> directory is passed then the default directory is checked
        /// </summary>
        /// <param name="name">The database name</param>
        /// <param name="directory">The directory where the database is located</param>
        /// <returns><c>true</c> if the database exists in the directory, otherwise <c>false</c></returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is <c>null</c></exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        public static bool Exists([@NotNull]string name, [@CanBeNull]string directory)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(name), name);

            return Directory.Exists(DatabasePath(name, directory));
        }

        /// <summary>
        /// Adds a change listener for the changes that occur in this database.  Signatures
        /// are the same as += style event handlers, but the callbacks will be called using the
        /// specified <see cref="TaskScheduler"/>.  If the scheduler is null, the default task
        /// scheduler will be used (scheduled via thread pool).
        /// </summary>
        /// <param name="scheduler">The scheduler to use when firing the change handler</param>
        /// <param name="handler">The handler to invoke</param>
        /// <returns>A <see cref="ListenerToken"/> that can be used to remove the handler later</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="handler"/> is <c>null</c></exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        public ListenerToken AddChangeListener([@CanBeNull]TaskScheduler scheduler,
            [@NotNull]EventHandler<DatabaseChangedEventArgs> handler)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(handler), handler);

            return ThreadSafety.DoLocked(() =>
            {
                CheckOpen();

                var cbHandler = new CouchbaseEventHandler<DatabaseChangedEventArgs>(handler, scheduler);
                if (_databaseChanged.Add(cbHandler) == 0) {
                    _obsContext = GCHandle.Alloc(this);
                    _obs = Native.c4dbobs_create(_c4db, _DatabaseObserverCallback, GCHandle.ToIntPtr(_obsContext).ToPointer());
                }

                return new ListenerToken(cbHandler, "db");
            });
        }

        /// <summary>
        /// Adds a change listener for the changes that occur in this database.  Signatures
        /// are the same as += style event handlers.  The callback will be invoked on a thread pool
        /// thread.
        /// </summary>
        /// <param name="handler">The handler to invoke</param>
        /// <returns>A <see cref="ListenerToken"/> that can be used to remove the handler later</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="handler"/> is <c>null</c></exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        public ListenerToken AddChangeListener([@NotNull]EventHandler<DatabaseChangedEventArgs> handler) => AddChangeListener(null, handler);

        /// <summary>
        /// Adds a document change listener for the document with the given ID and the <see cref="TaskScheduler"/>
        /// that will be used to invoke the callback.  If the scheduler is not specified, then the default scheduler
        /// will be used (scheduled via thread pool)
        /// </summary>
        /// <param name="id">The document ID</param>
        /// <param name="scheduler">The scheduler to use when firing the event handler</param>
        /// <param name="handler">The logic to handle the event</param>
        /// <returns>A <see cref="ListenerToken"/> that can be used to remove the listener later</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="handler"/> or <paramref name="id"/>
        /// is <c>null</c></exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        public ListenerToken AddDocumentChangeListener([@NotNull]string id, [@CanBeNull]TaskScheduler scheduler,
            [@NotNull]EventHandler<DocumentChangedEventArgs> handler)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(id), id);
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(handler), handler);

            return ThreadSafety.DoLocked(() =>
            {
                CheckOpen();

                var cbHandler =
                    new CouchbaseEventHandler<string, DocumentChangedEventArgs>(handler, id, scheduler);
                var count = _documentChanged.Add(cbHandler);
                if (count == 0) {
                    var handle = GCHandle.Alloc(this);
                    var docObs = Native.c4docobs_create(_c4db, id, _DocumentObserverCallback, GCHandle.ToIntPtr(handle).ToPointer());
                    _docObs[id] = Tuple.Create((IntPtr) docObs, handle);
                }

                return new ListenerToken(cbHandler, "doc");
            });
        }

        /// <summary>
        /// Adds a document change listener for the document with the given ID.  The callback will be
        /// invoked on a thread pool thread.
        /// </summary>
        /// <param name="id">The document ID</param>
        /// <param name="handler">The logic to handle the event</param>
        /// <returns>A <see cref="ListenerToken"/> that can be used to remove the listener later</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="handler"/> or <paramref name="id"/>
        /// is <c>null</c></exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        public ListenerToken AddDocumentChangeListener([@NotNull]string id, [@NotNull]EventHandler<DocumentChangedEventArgs> handler) => AddDocumentChangeListener(id, null, handler);

        /// <summary>
        /// Close database synchronously. Before closing the database, the active replicators, listeners and live queries will be stopped.
        /// </summary>
        /// <exception cref="CouchbaseLiteException">Thrown with <see cref="C4ErrorCode.Busy"/> if there are still active replicators
        /// or query listeners when the close call occurred</exception>
        public void Close() => Dispose();

        /// <summary>
        /// Performs database maintenance.
        /// </summary>
        /// <param name="type">Maintenance type</param>
        public void PerformMaintenance(MaintenanceType type)
        {
            ThreadSafety.DoLockedBridge(err =>
            {
                CheckOpen();
                return Native.c4db_maintenance(_c4db, (C4MaintenanceType) type, err);
            });
        }

        /// <summary>
        /// Creates an index which could be a value index from <see cref="IndexBuilder.ValueIndex"/> or a full-text search index
        /// from <see cref="IndexBuilder.FullTextIndex"/> with the given name.
        /// The name can be used for deleting the index. Creating a new different index with an existing
        /// index name will replace the old index; creating the same index with the same name will be no-ops.
        /// </summary>
        /// <param name="name">The index name</param>
        /// <param name="index">The index</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> or <paramref name="index"/>
        /// is <c>null</c></exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        /// <exception cref="NotSupportedException">Thrown if an implementation of <see cref="IIndex"/> other than one of the library
        /// provided ones is used</exception>
        public void CreateIndex([@NotNull]string name, [@NotNull]IIndex index)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(name), name);
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(index), index);

            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
                var concreteIndex = Misc.TryCast<IIndex, QueryIndex>(index);
                var jsonObj = concreteIndex.ToJSON();
                var json = JsonConvert.SerializeObject(jsonObj);
                LiteCoreBridge.Check(err =>
                {
                    var internalOpts = concreteIndex.Options;

                    // For some reason a "using" statement here causes a compiler error
                    try {
                        return Native.c4db_createIndex2(c4db, name, json, C4QueryLanguage.JSONQuery, concreteIndex.IndexType, &internalOpts, err);
                    } finally {
                        internalOpts.Dispose();
                    }
                });
            });
        }

        /// <summary>
        /// Creates a N1QL query index which could be a value index from <see cref="ValueIndexConfiguration"/> or a full-text search index
        /// from <see cref="FullTextIndexConfiguration"/> with the given name.
        /// The name can be used for deleting the index. Creating a new different index with an existing
        /// index name will replace the old index; creating the same index with the same name will be no-ops.
        /// </summary>
        /// <param name="name">The index name</param>
        /// <param name="indexConfig">The index</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> or <paramref name="indexConfig"/>
        /// is <c>null</c></exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        /// <exception cref="NotSupportedException">Thrown if an implementation of <see cref="IIndex"/> other than one of the library
        /// provided ones is used</exception>
        public void CreateIndex([@NotNull] string name, [@NotNull] IndexConfiguration indexConfig)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(name), name);
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(indexConfig), indexConfig);

            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
                LiteCoreBridge.Check(err =>
                {
                    var internalOpts = indexConfig.Options;
                    // For some reason a "using" statement here causes a compiler error
                    try {
                        return Native.c4db_createIndex2(c4db, name, indexConfig.ToN1QL(), indexConfig.QueryLanguage, indexConfig.IndexType, &internalOpts, err);
                    } finally  {
                        internalOpts.Dispose();
                    }
                });
            });
        }
        
        /// Creates a Query object from the given N1QL query string.
        /// </summary>
        /// <param name="queryExpression">N1QL Query Expression</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="queryExpression"/>
        /// is <c>null</c></exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        public IQuery CreateQuery([@NotNull]string queryExpression)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(queryExpression), queryExpression);
            var query = new NQuery(queryExpression, this);
            return query;
        }

        /// <summary>
        /// Close and delete the database synchronously. Before closing the database, the active replicators, listeners and live queries will be stopped.
        /// </summary>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        /// <exception cref="CouchbaseLiteException">Thrown with <see cref="C4ErrorCode.Busy"/> if there are still active replicators
        /// or query listeners when the close call occurred</exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        public void Delete()
        {
            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
            });

            Close();
            Delete(Name, Config.Directory);
        }

        /// <summary>
        /// Deletes a document from the database.  When write operations are executed
        /// concurrently, the last writer will overwrite all other written values.
        /// Calling this method is the same as calling <see cref="Delete(Document, ConcurrencyControl)"/>
        /// with <see cref="ConcurrencyControl.LastWriteWins"/>
        /// </summary>
        /// <param name="document">The document</param>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        /// <exception cref="CouchbaseLiteException">Thrown with <see cref="C4ErrorCode.InvalidParameter"/>
        /// when trying to save a document into a database other than the one it was previously added to</exception>
        /// <exception cref="CouchbaseLiteException">Thrown with <see cref="C4ErrorCode.NotFound"/>
        /// when trying to delete a document that hasn't been saved into a <see cref="Database"/> yet</exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        public void Delete([@NotNull]Document document) => Delete(document, ConcurrencyControl.LastWriteWins);

        /// <summary>
        /// Deletes the given <see cref="Document"/> from this database
        /// </summary>
        /// <param name="document">The document to save</param>
        /// <param name="concurrencyControl">The rule to use when encountering a conflict in the database</param>
        /// <returns><c>true</c> if the delete succeeded, <c>false</c> if there was a conflict</returns>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        /// <exception cref="CouchbaseLiteException">Thrown with <see cref="C4ErrorCode.InvalidParameter"/>
        /// when trying to save a document into a database other than the one it was previously added to</exception>
        /// <exception cref="CouchbaseLiteException">Thrown with <see cref="C4ErrorCode.NotFound"/>
        /// when trying to delete a document that hasn't been saved into a <see cref="Database"/> yet</exception>
        /// <exception cref="InvalidOperationException">Thrown if this method is called after the database is closed</exception>
        public bool Delete([@NotNull]Document document, ConcurrencyControl concurrencyControl)
        {
            var doc = CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(document), document);
            return Save(doc, null, concurrencyControl, true);
        }

        /// <summary>
        /// Deletes the index with the given name
        /// </summary>
        /// <param name="name">The name of the index to delete</param>
        public void DeleteIndex([@NotNull]string name)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(name), name);

            ThreadSafety.DoLockedBridge(err =>
            {
                CheckOpen();
                return Native.c4db_deleteIndex(c4db, name, err);
            });
        }

        /// <summary>
        /// Gets the <see cref="Document"/> with the specified ID
        /// </summary>
        /// <param name="id">The ID to use when creating or getting the document</param>
        /// <returns>The instantiated document, or <c>null</c> if it does not exist</returns>
        [@CanBeNull]
        public Document GetDocument([@NotNull]string id)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(id), id);
            return ThreadSafety.DoLocked(() => GetDocumentInternal(id));
        }

        /// <summary>
        /// Gets a list of index names that are present in the database
        /// </summary>
        /// <returns>The list of created index names</returns>
        [@NotNull]
        [@ItemNotNull]
        public IList<string> GetIndexes()
        {
            List<string> retVal = new List<string>();
            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
                var result = new FLSliceResult();
                LiteCoreBridge.Check(err =>
                {
                    result = NativeRaw.c4db_getIndexesInfo(c4db, err);
                    return result.buf != null;
                });

                var val = NativeRaw.FLValue_FromData(new FLSlice(result.buf, result.size), FLTrust.Trusted);
                if (val == null) {
                    Native.FLSliceResult_Release(result);
                    throw new CouchbaseLiteException(C4ErrorCode.UnexpectedError);
                }

                var indexesInfo = FLValueConverter.ToCouchbaseObject(val, this, true) as IList<object>;
                foreach (var a in indexesInfo) {
                    var indexInfo = a as Dictionary<string, object>;
                    retVal.Add((string)indexInfo["name"]);
                }

                Native.FLSliceResult_Release(result);
            });

            return retVal as IList<string> ?? new List<string>();
        }

        /// <summary>
        /// Runs the given batch of operations as an atomic unit
        /// </summary>
        /// <param name="action">The <see cref="Action"/> containing the operations. </param>
        public void InBatch([@NotNull]Action action)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(action), action);

            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
                PerfTimer.StartEvent("InBatch_BeginTransaction");
                LiteCoreBridge.Check(err => Native.c4db_beginTransaction(_c4db, err));
                PerfTimer.StopEvent("InBatch_BeginTransaction");
                var success = true;
                try {
                    action();
                } catch (Exception e) {
                    WriteLog.To.Database.W(Tag, "Exception during InBatch, rolling back...", e);
                    success = false;
                    throw;
                } finally {
                    PerfTimer.StartEvent("InBatch_EndTransaction");
                    LiteCoreBridge.Check(err => Native.c4db_endTransaction(_c4db, success, err));
                    PerfTimer.StopEvent("InBatch_EndTransaction");
                }
            });

            PostDatabaseChanged();
        }

        /// <summary>
        /// Purges the given <see cref="Document"/> from the database.  This leaves
        /// no trace behind and will not be replicated
        /// </summary>
        /// <param name="document">The document to purge</param>
        /// <exception cref="InvalidOperationException">Thrown when trying to purge a document from a database
        /// other than the one it was previously added to</exception>
        public void Purge([@NotNull]Document document)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(document), document);
            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
                VerifyDB(document);

                if (!document.Exists) {
                    throw new CouchbaseLiteException(C4ErrorCode.NotFound);
                }

                InBatch(() => PurgeDocById(document.Id));
            });
        }

        /// <summary>
        /// Purges the given document id of the <see cref="Document"/> 
        /// from the database.  This leaves no trace behind and will 
        /// not be replicated
        /// </summary>
        /// <param name="docId">The id of the document to purge</param>
        /// <exception cref="C4ErrorCode.NotFound">Throws NOT FOUND error if the document 
        /// of the docId doesn't exist.</exception>
        public void Purge([@NotNull]string docId)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(docId), docId);
            InBatch(() => PurgeDocById(docId));
        }

        /// <summary>
        /// Sets an expiration date on a document. After this time, the document
        /// will be purged from the database.
        /// </summary>
        /// <param name="docId"> The ID of the <see cref="Document"/> </param> 
        /// <param name="expiration"> Nullable expiration timestamp as a 
        /// <see cref="DateTimeOffset"/>, set timestamp to <c>null</c> 
        /// to remove expiration date time from doc.</param>
        /// <returns>Whether successfully sets an expiration date on the document</returns>
        /// <exception cref="CouchbaseLiteException">Throws NOT FOUND error if the document 
        /// doesn't exist</exception>
        public bool SetDocumentExpiration(string docId, DateTimeOffset? expiration)
        {
            var succeed = false;
            ThreadSafety.DoLockedBridge(err =>
            {
                if (expiration == null) {
                    succeed = Native.c4doc_setExpiration(_c4db, docId, 0, err);
                } else {
                    var millisSinceEpoch = expiration.Value.ToUnixTimeMilliseconds();
                    succeed = Native.c4doc_setExpiration(_c4db, docId, millisSinceEpoch, err);
                }

                return succeed;
            });
            return succeed;
        }

        /// <summary>
        /// Returns the expiration time of the document. <c>null</c> will be returned
        /// if there is no expiration time set
        /// </summary>
        /// <param name="docId"> The ID of the <see cref="Document"/> </param>
        /// <returns>Nullable expiration timestamp as a <see cref="DateTimeOffset"/> 
        /// of the document or <c>null</c> if time not set. </returns>
        /// <exception cref="CouchbaseLiteException">Throws NOT FOUND error if the document 
        /// doesn't exist</exception>
        public DateTimeOffset? GetDocumentExpiration(string docId)
        {
            if (LiteCoreBridge.Check(err => Native.c4db_getDoc(_c4db, docId, true, C4DocContentLevel.DocGetCurrentRev, err)) == null) {
                throw new CouchbaseLiteException(C4ErrorCode.NotFound);
            }

            C4Error err2 = new C4Error();
            var res = (long) Native.c4doc_getExpiration(_c4db, docId, &err2);
            if (res == 0) {
                if (err2.code == 0) {
                    return null;
                }

                throw CouchbaseException.Create(err2);
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(res);
        }

        /// <summary>
        /// Removes a database changed listener by token
        /// </summary>
        /// <param name="token">The token received from <see cref="AddChangeListener(TaskScheduler, EventHandler{DatabaseChangedEventArgs})"/>
        /// and family</param>
        public void RemoveChangeListener(ListenerToken token)
        {
            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();

                if (token.Type == "db") {
                    if (_databaseChanged.Remove(token) == 0) {
                        Native.c4dbobs_free(_obs);
                        _obs = null;
                        if (_obsContext.IsAllocated) {
                            _obsContext.Free();
                        }
                    }
                } else {
                    if (_documentChanged.Remove(token, out var docID) == 0) {
                        if (_docObs.TryGetValue(docID, out var observer)) {
                            _docObs.Remove(docID);
                            Native.c4docobs_free((C4DocumentObserver*) observer.Item1);
                            observer.Item2.Free();
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Saves the given <see cref="MutableDocument"/> into this database.  This call is equivalent to calling
        /// <see cref="Save(MutableDocument, ConcurrencyControl)" /> with a second argument of
        /// <see cref="ConcurrencyControl.LastWriteWins"/>
        /// </summary>
        /// <param name="document">The document to save</param>
        /// <exception cref="InvalidOperationException">Thrown when trying to save a document into a database
        /// other than the one it was previously added to</exception>
        public void Save([@NotNull]MutableDocument document) => Save(document, ConcurrencyControl.LastWriteWins);

        /// <summary>
        /// Saves the given <see cref="MutableDocument"/> into this database
        /// </summary>
        /// <param name="document">The document to save</param>
        /// <param name="concurrencyControl">The rule to use when encountering a conflict in the database</param>
        /// <exception cref="InvalidOperationException">Thrown when trying to save a document into a database
        /// other than the one it was previously added to</exception>
        /// <returns><c>true</c> if the save succeeded, <c>false</c> if there was a conflict</returns>
        public bool Save([@NotNull]MutableDocument document, ConcurrencyControl concurrencyControl)
        {
            var doc = CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(document), document);
            return Save(doc, null, concurrencyControl, false);
        }

        /// <summary>
        /// Saves a document to the database. When write operations are executed concurrently, 
        /// and if conflicts occur, conflict handler will be called. Use the handler to directly
        /// edit the document.Returning true, will save the document. Returning false, will cancel
        /// the save operation.
        /// </summary>
        /// <param name="document">The document to save</param>
        /// <param name="conflictHandler">The conflict handler block which can be used to resolve it.</param> 
        /// <returns><c>true</c> if the save succeeded, <c>false</c> if there was a conflict</returns>
        public bool Save([@NotNull]MutableDocument document, [@NotNull]Func<MutableDocument, Document, bool> conflictHandler)
        {
            var doc = CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(document), document);
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(conflictHandler), conflictHandler);
            Document baseDoc = null;
            var saved = false;
            do {
                saved = Save(doc, baseDoc, ConcurrencyControl.FailOnConflict, false);
                baseDoc = new Document(this, doc.Id);
                if (!baseDoc.Exists) {
                    throw new CouchbaseLiteException(C4ErrorCode.NotFound);
                }
                if (!saved) {
                    try {
                        if (!conflictHandler(doc, baseDoc.IsDeleted ? null : baseDoc)) { // resolve conflict with conflictHandler
                            return false;
                        }
                    } catch {
                        return false;
                    }
                }
            } while (!saved);// has conflict, save failed
            return saved;
        }

        /// <summary>
        /// Save a blob object directly into the database without associating it with any documents.
        /// </summary>
        /// <remarks>The blobs that are not associated with any documents will be removed from the database when compacting the database.</remarks>
        /// <exception cref="CouchbaseLiteException">Thrown if an error occurs during the blob save operation.</exception>
        /// <param name="blob">The blob object will be saved into Database.</param>
        public void SaveBlob(Blob blob)
        {
            blob.Install(this);
        }

        /// <summary>
        /// Gets the <see cref="Blob"/> of a given blob dictionary.
        /// </summary>
        /// <remarks>The blobs that are not associated with any documents are/will be removed from the database after compacting the database.</remarks>
        /// <param name="blobDict"> 
        /// JSON Dictionary represents in the <see cref="Blob"/> and the value will be validated in <see cref="Blob.IsBlob(IDictionary{string, object})"/>
        /// </param>
        /// <exception cref="ArgumentException">Throw if the given blob dictionary is not valid.</exception>
        /// <returns>The contained value, or <c>null</c> if it's digest information doesn’t exist.</returns>
        [CanBeNull]
        public Blob GetBlob(Dictionary<string, object> blobDict)
        {
            if (!blobDict.ContainsKey(Blob.DigestKey) || blobDict[Blob.DigestKey] == null)
                return null;

            if (!Blob.IsBlob(blobDict)) {
                throw new ArgumentException(CouchbaseLiteErrorMessage.InvalidJSONDictionaryForBlob);
            }

            C4BlobKey expectedKey = new C4BlobKey();
            var keyFromStr = Native.c4blob_keyFromString((string)blobDict[Blob.DigestKey], &expectedKey);
            if (!keyFromStr) {
                return null;
            }

            var size = Native.c4blob_getSize(BlobStore, expectedKey);
            if (size == -1) {
                return null;
            }

            return new Blob(this, blobDict);
        }

#if CBL_LINQ
        public void Save(Couchbase.Lite.Linq.IDocumentModel model)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Database, Tag, nameof(model), model);

            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
                MutableDocument md = (model.Document as MutableDocument) ?? model.Document?.ToMutable() ?? new MutableDocument();
                md.SetFromModel(model);

                try {
                    var retVal = Save(md, false);
                    model.Document = retVal;
                } finally {
                    md.Dispose();
                }
            });
        }
#endif

        #endregion

        #region Internal Methods

        internal void AddActiveStoppable(IStoppable stoppable)
        {
            ThreadSafety.DoLocked(() =>
            {
                CheckOpenAndNotClosing();
                if(ActiveStoppables.TryAdd(stoppable, 0)) {
                    _closeCondition.Reset();
                }
            });
        }

        internal void RemoveActiveStoppable(IStoppable stoppable)
        {
            ThreadSafety.DoLocked(() =>
            {
                if (IsClosed) {
                    return;
                }

                if(!ActiveStoppables.TryRemove(stoppable, out var dummy)) {
                    return;
                }

                if (ActiveStoppables.Count == 0) {
                    _closeCondition.Set();
                }
            });
        }

        internal string GetCookies([@NotNull] Uri uri)
        {
            string cookies = null;
            ThreadSafety.DoLocked(() =>
            {
                if (uri == null) {
                    WriteLog.To.Sync.V(Tag, "The Uri used to get cookies is null.");
                } else {
                    var addr = new C4Address();
                    var scheme = new C4String();
                    var host = new C4String();
                    var path = new C4String();
                    var pathStr = String.Concat(uri.Segments.Take(uri.Segments.Length - 1));
                    scheme = new C4String(uri.Scheme);
                    host = new C4String(uri.Host);
                    path = new C4String(pathStr);
                    addr.scheme = scheme.AsFLSlice();
                    addr.hostname = host.AsFLSlice();
                    addr.port = (ushort) uri.Port;
                    addr.path = path.AsFLSlice();

                    C4Error err = new C4Error();
                    cookies = Native.c4db_getCookies(_c4db, addr, &err);
                    if (err.code > 0) {
                        WriteLog.To.Sync.W(Tag, $"{err.domain}/{err.code} Failed getting Cookie from address {addr}.");
                    }

                    if (String.IsNullOrEmpty(cookies) && err.code == 0) {
                        WriteLog.To.Sync.V(Tag, "There is no saved HTTP cookies.");
                    }
                }
            });

            return cookies;
        }

        internal bool SaveCookie(string cookie, [@NotNull] Uri uri)
        {
            bool cookieSaved = false;
            ThreadSafety.DoLocked(() =>
            {
                if (uri == null) {
                    WriteLog.To.Sync.V(Tag, "The Uri used to set cookie is null.");
                } else {
                    var cookieStr = cookie.ToCBLCookieString();
                    var pathStr = String.Concat(uri.Segments.Take(uri.Segments.Length - 1));
                    C4Error err = new C4Error();
                    cookieSaved = Native.c4db_setCookie(_c4db, cookieStr, uri.Host, pathStr, &err);
                    if(err.code > 0) {
                        WriteLog.To.Sync.W(Tag, $"{err.domain}/{err.code} Failed saving Cookie {cookieStr}.");
                    }
                }
            });

            return cookieSaved;
        }

        internal void ResolveConflict([@NotNull]string docID, [@CanBeNull]IConflictResolver conflictResolver)
        {
            Debug.Assert(docID != null);
            var writeSuccess = false;
            while (!writeSuccess) {
                var readSuccess = false;
                Document localDoc = null, remoteDoc = null, resolvedDoc = null;
                try {
                    InBatch(() =>
                    {
                        // Do this in a batch so that there are no changes to the document between
                        // localDoc read and remoteDoc read
                        localDoc = new Document(this, docID);
                        if (!localDoc.Exists) {
                            throw new CouchbaseLiteException(C4ErrorCode.NotFound);
                        }

                        remoteDoc = new Document(this, docID, C4DocContentLevel.DocGetAll);
                        if (!remoteDoc.Exists || !remoteDoc.SelectConflictingRevision()) {
                            WriteLog.To.Sync.W(Tag, "Unable to select conflicting revision for '{0}', the conflict may have been previously resolved...",
                                new SecureLogString(docID, LogMessageSensitivity.PotentiallyInsecure));
                            return;
                        }

                        readSuccess = true;
                    });

                    if (!readSuccess) {
                        return;
                    }

                    if (localDoc.IsDeleted && remoteDoc.IsDeleted) {
                        resolvedDoc = localDoc; // No need go through resolver, because both remote and local docs are deleted.
                    } else {
                        // Resolve conflict:
                        WriteLog.To.Database.I(Tag, "Resolving doc '{0}' (mine={1} and theirs={2})",
                                new SecureLogString(docID, LogMessageSensitivity.PotentiallyInsecure), localDoc.RevisionID,
                                remoteDoc.RevisionID);

                        conflictResolver = conflictResolver ?? ConflictResolver.Default;
                        var conflict = new Conflict(docID, localDoc.IsDeleted ? null : localDoc, remoteDoc.IsDeleted ? null : remoteDoc);

                        resolvedDoc = conflictResolver.Resolve(conflict);
                    }

                    if (resolvedDoc != null) {
                        if (resolvedDoc.Id != docID) {
                            WriteLog.To.Sync.W(Tag, $"Resolved docID {resolvedDoc.Id} does not match docID {docID}",
                                new SecureLogString(docID, LogMessageSensitivity.PotentiallyInsecure));
                            Misc.SafeSwap(ref resolvedDoc, new MutableDocument(docID, resolvedDoc.ToDictionary()));
                        }
                        if (resolvedDoc.Database == null) {
                            resolvedDoc.Database = this;
                        } else if (resolvedDoc.Database != this) {
                            throw new InvalidOperationException(String.Format(CouchbaseLiteErrorMessage.ResolvedDocWrongDb,
                                resolvedDoc.Database.Name, this.Name));
                        }
                    }

                    InBatch(() =>
                    {
                        // ReSharper disable once AccessToDisposedClosure
                        writeSuccess = SaveResolvedDocument(resolvedDoc, localDoc, remoteDoc);
                    });
                } finally {
                    resolvedDoc?.Dispose();
                    localDoc?.Dispose();
                    remoteDoc?.Dispose();
                }
            }
        }


        internal void CheckOpenLocked()
        {
            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
            });
        }

        #endregion

        #region Private Methods

        [@NotNull]
        private static string DatabasePath(string directory)
        {
            var directoryToUse = String.IsNullOrWhiteSpace(directory)
                ? Service.GetRequiredInstance<IDefaultDirectoryResolver>().DefaultDirectory()
                : directory;

            if (String.IsNullOrWhiteSpace(directoryToUse)) {
                throw new RuntimeException(
                    CouchbaseLiteErrorMessage.ResolveDefaultDirectoryFailed);
            }

            return directoryToUse;
        }

        private static string DatabasePath(string name, string directory)
        {
            var directoryToUse = DatabasePath(directory);

            if (String.IsNullOrWhiteSpace(name))
            {
                return directoryToUse;
            }

            return System.IO.Path.Combine(directoryToUse, $"{name}.{DBExtension}") ??
                throw new RuntimeException("Path.Combine failed to return a non-null value!");
        }

        #if __IOS__
        [ObjCRuntime.MonoPInvokeCallback(typeof(C4DatabaseObserverCallback))]
        #endif
        private static void DbObserverCallback(C4DatabaseObserver* db, void* context)
        {
            var dbObj = GCHandle.FromIntPtr((IntPtr) context).Target as Database;
            dbObj?._callbackFactory.StartNew(() =>
            {
                dbObj.PostDatabaseChanged();
            });
        }

        #if __IOS__
        [ObjCRuntime.MonoPInvokeCallback(typeof(C4DocumentObserverCallback))]
        #endif
        private static void DocObserverCallback(C4DocumentObserver* obs, FLSlice docId, ulong sequence, void* context)
        {
            if (docId.buf == null) {
                return;
            }

            var dbObj = GCHandle.FromIntPtr((IntPtr) context).Target as Database;
            dbObj?._callbackFactory.StartNew(() =>
            {
                dbObj.PostDocChanged(docId.CreateString());
            });
        }

        // Must be called inside self lock
        private void CheckOpen()
        {
            if (IsClosed) {
                throw new InvalidOperationException(CouchbaseLiteErrorMessage.DBClosed);
            }
        }

        private void Dispose(bool disposing)
        {
            if (IsClosed) {
                return;
            }

            if (disposing) {
                ClearUnsavedDocsAndFreeDocObservers();
            }

            FreeC4DbObserver();

            WriteLog.To.Database.I(Tag, $"Closing database at path {Native.c4db_getPath(_c4db)}");
            if (!IsShell) {
                LiteCoreBridge.Check(err => Native.c4db_close(_c4db, err));
            }

            FreeC4Db();

            _closeCondition.Dispose();
        }

        [@CanBeNull]
        private Document GetDocumentInternal([@NotNull]string docID)
        {
            CheckOpen();
            var doc = new Document(this, docID);

            if (!doc.Exists || doc.IsDeleted) {
                doc.Dispose();
                WriteLog.To.Database.V(Tag, "Requested existing document {0}, but it doesn't exist", 
                    new SecureLogString(docID, LogMessageSensitivity.PotentiallyInsecure));
                return null;
            }

            return doc;
        }

        private void Open()
        {
            if (_c4db != null) {
                return;
            }

            try {
                Directory.CreateDirectory(Config.Directory);
            } catch (Exception e) {
                throw new CouchbaseLiteException(C4ErrorCode.CantOpenFile, 
                    CouchbaseLiteErrorMessage.CreateDBDirectoryFailed, e);
            }

            var config = DBConfig;
            config.ParentDirectory = Config.Directory;

            var encrypted = "";

            #if COUCHBASE_ENTERPRISE
            if (Config.EncryptionKey != null) {
                var key = Config.EncryptionKey;
                var i = 0;
                config.encryptionKey.algorithm = C4EncryptionAlgorithm.AES256;
                foreach (var b in key.KeyData) {
                    config.encryptionKey.bytes[i++] = b;
                }

                encrypted = "encrypted ";
            }
            #endif

            WriteLog.To.Database.I(Tag, $"Opening {encrypted} database at { DatabasePath(Name, Config.Directory)}");
            var localConfig1 = config;
            ThreadSafety.DoLocked(() =>
            {
                _c4db = (C4Database*) LiteCoreBridge.Check(err =>
                {
                    var localConfig2 = localConfig1;
                    return Native.c4db_openNamed(Name, &localConfig2, err);
                });
            });
        }

        private void PostDatabaseChanged()
        {
            ThreadSafety.DoLocked(() =>
            {
                if (_obs == null || IsClosed) {
                    return;
                }

                const uint maxChanges = 100u;
                var external = false;
                uint nChanges;
                var changes = new C4DatabaseChange[maxChanges];
                var docIDs = new List<string>();
                do {
                    // Read changes in batches of MaxChanges:
                    bool newExternal;
                    nChanges = Native.c4dbobs_getChanges(_obs, changes, maxChanges, &newExternal);
                    if (nChanges == 0 || external != newExternal || docIDs.Count > 1000) {
                        if (docIDs.Count > 0) {
                            // Only notify if there are actually changes to send
                            var args = new DatabaseChangedEventArgs(this, docIDs);
                            _databaseChanged.Fire(this, args);
                            docIDs = new List<string>();
                        }
                    }

                    external = newExternal;
                    for (var i = 0; i < nChanges; i++) {
                        docIDs.Add(changes[i].docID.CreateString());
                    }
                    Native.c4dbobs_releaseChanges(changes, nChanges);
                } while (nChanges > 0);
            });
        }

        private void PostDocChanged([@NotNull]string documentID)
        {
            DocumentChangedEventArgs change = null;
            ThreadSafety.DoLocked(() =>
            {
                if (IsClosed || !_docObs.ContainsKey(documentID)) {
                    return;
                }

                change = new DocumentChangedEventArgs(documentID, this);
            });

            _documentChanged.Fire(documentID, this, change);
        }

        private bool Save([@NotNull]Document document, [@CanBeNull]Document baseDocument,
            ConcurrencyControl concurrencyControl, bool deletion)
        {
            Debug.Assert(document != null);
            if (deletion && document.RevisionID == null) {
                throw new CouchbaseLiteException(C4ErrorCode.NotFound,
                    CouchbaseLiteErrorMessage.DeleteDocFailedNotSaved);
            }

            var success = true;
            ThreadSafety.DoLocked(() =>
            {
                CheckOpen();
                VerifyDB(document);
                C4Document* curDoc = null;
                C4Document* newDoc = null;
                var committed = false;
                try {
                    LiteCoreBridge.Check(err => Native.c4db_beginTransaction(_c4db, err));
                    var baseDoc = baseDocument == null ? null : baseDocument.c4Doc.RawDoc;
                    Save(document, &newDoc, baseDoc, deletion);
                    if (newDoc == null) {
                        // Handle conflict:
                        if (concurrencyControl == ConcurrencyControl.FailOnConflict) {
                            success = false;
                            committed = true; // Weird, but if the next call fails I don't want to call it again in the catch block
                            LiteCoreBridge.Check(e => Native.c4db_endTransaction(_c4db, true, e));
                            return;
                        }

                        C4Error err;
                        curDoc = Native.c4db_getDoc(_c4db, document.Id, true, C4DocContentLevel.DocGetCurrentRev, &err);

                        // If deletion and the current doc has already been deleted
                        // or doesn't exist:
                        if (deletion) {
                            if (curDoc == null) {
                                if (err.code == (int) C4ErrorCode.NotFound) {
                                    return;
                                }

                                throw CouchbaseException.Create(err);
                            } else if (curDoc->flags.HasFlag(C4DocumentFlags.DocDeleted)) {
                                document.ReplaceC4Doc(new C4DocumentWrapper(curDoc));
                                curDoc = null;
                                return;

                            }
                        }

                        // Save changes on the current branch:
                        if (curDoc == null) {
                            throw CouchbaseException.Create(err);
                        }

                        Save(document, &newDoc, curDoc, deletion);
                    }

                    committed = true; // Weird, but if the next call fails I don't want to call it again in the catch block
                    LiteCoreBridge.Check(e => Native.c4db_endTransaction(_c4db, true, e));
                    document.ReplaceC4Doc(new C4DocumentWrapper(newDoc));
                    newDoc = null;
                } catch (Exception) {
                    if (!committed) {
                        LiteCoreBridge.Check(e => Native.c4db_endTransaction(_c4db, false, e));
                    }

                    throw;
                } finally {
                    Native.c4doc_release(curDoc);
                    Native.c4doc_release(newDoc);
                }
            });

            return success;
        }

        private void Save([@NotNull]Document doc, C4Document** outDoc, C4Document* baseDoc, bool deletion)
        {
            var revFlags = (C4RevisionFlags) 0;
            if (deletion) {
                revFlags = C4RevisionFlags.Deleted;
            }

            var body = (FLSliceResult) FLSlice.Null;
            if (!deletion && !doc.IsEmpty) {
                try {
                    body = doc.Encode();
                } catch (ObjectDisposedException) {
                    WriteLog.To.Database.E(Tag, "Save of disposed document {0} attempted, skipping...", new SecureLogString(doc.Id, LogMessageSensitivity.PotentiallyInsecure));
                    return;
                }

                FLDoc* fleeceDoc = Native.FLDoc_FromResultData(body,
                    FLTrust.Trusted,
                    Native.c4db_getFLSharedKeys(_c4db), FLSlice.Null);
                ThreadSafety.DoLocked(() =>
                {
                    if (Native.c4doc_dictContainsBlobs((FLDict*) Native.FLDoc_GetRoot(fleeceDoc))) {
                        revFlags |= C4RevisionFlags.HasAttachments;
                    }

                    Native.FLDoc_Release(fleeceDoc);
                });
            } else if (doc.IsEmpty) {
                body = EmptyFLSliceResult();
            }

            var rawDoc = baseDoc != null ? baseDoc :
                doc.c4Doc?.HasValue == true ? doc.c4Doc.RawDoc : null;
            if (rawDoc != null) {
                doc.ThreadSafety.DoLocked(() =>
                {
                    ThreadSafety.DoLocked(() =>
                    {
                        *outDoc = (C4Document*) NativeHandler.Create()
                            .AllowError((int) C4ErrorCode.Conflict, C4ErrorDomain.LiteCoreDomain).Execute(
                                err => NativeRaw.c4doc_update(rawDoc, (FLSlice) body, revFlags, err));
                    });
                });
            } else {
                ThreadSafety.DoLocked(() =>
                {
                    using (var docID_ = new C4String(doc.Id)) {
                        *outDoc = (C4Document*) NativeHandler.Create()
                            .AllowError((int) C4ErrorCode.Conflict, C4ErrorDomain.LiteCoreDomain).Execute(
                                err => NativeRaw.c4doc_create(_c4db, docID_.AsFLSlice(), (FLSlice) body, revFlags, err));
                    }
                });
            }

            Native.FLSliceResult_Release(body);
        }

        // Must be called in transaction
        private bool SaveResolvedDocument([@CanBeNull]Document resolvedDoc, [@NotNull]Document localDoc, [@NotNull]Document remoteDoc)
        {
            if (resolvedDoc == null) {
                if (localDoc.IsDeleted)
                    resolvedDoc = localDoc;

                if (remoteDoc.IsDeleted)
                    resolvedDoc = remoteDoc;
            }

            if (resolvedDoc != null && !ReferenceEquals(resolvedDoc, localDoc)) {
                resolvedDoc.Database = this;
            }

            // The remote branch has to win, so that the doc revision history matches the server's.
            var winningRevID = remoteDoc.RevisionID;
            var losingRevID = localDoc.RevisionID;

            // mergedBody:
            FLSliceResult mergedBody = (FLSliceResult) FLSlice.Null;
            if (!ReferenceEquals(resolvedDoc, remoteDoc)) {
                if (resolvedDoc != null) {
                    // Unless the remote revision is being used as-is, we need a new revision:
                    mergedBody = resolvedDoc.Encode();
                    if (mergedBody.Equals((FLSliceResult) FLSlice.Null))
                        throw new RuntimeException(CouchbaseLiteErrorMessage.ResolvedDocContainsNull);
                } else {
                    mergedBody = EmptyFLSliceResult();
                }
            }

            // mergedFlags:
            C4RevisionFlags mergedFlags = resolvedDoc?.c4Doc != null ? resolvedDoc.c4Doc.RawDoc->selectedRev.flags : 0;
            if (resolvedDoc == null || resolvedDoc.IsDeleted)
                mergedFlags |= C4RevisionFlags.Deleted;

            // Tell LiteCore to do the resolution:
            C4Document* rawDoc = localDoc.c4Doc != null ? localDoc.c4Doc.RawDoc : null;
            using (var winningRevID_ = new C4String(winningRevID))
            using (var losingRevID_ = new C4String(losingRevID)) {
                C4Error err;
                var retVal = NativeRaw.c4doc_resolveConflict(rawDoc, winningRevID_.AsFLSlice(),
                    losingRevID_.AsFLSlice(), (FLSlice) mergedBody, mergedFlags, &err)
                    && Native.c4doc_save(rawDoc, 0, &err);
                Native.FLSliceResult_Release(mergedBody);

                if (!retVal) {
                    if (err.code == (int) C4ErrorCode.Conflict) {
                        return false;
                    } else {
                        throw new CouchbaseLiteException((C4ErrorCode) err.code,
                            CouchbaseLiteErrorMessage.ResolvedDocFailedLiteCore);
                    }
                }
            }

            WriteLog.To.Database.I(Tag, "Conflict resolved as doc '{0}' rev {1}",
                new SecureLogString(localDoc.Id, LogMessageSensitivity.PotentiallyInsecure),
                rawDoc->revID.CreateString());

            return true;
        }

        private FLSliceResult EmptyFLSliceResult()
        {
            FLEncoder* encoder = SharedEncoder;
            Native.FLEncoder_BeginDict(encoder, 0);
            Native.FLEncoder_EndDict(encoder);
            var body = NativeRaw.FLEncoder_Finish(encoder, null);
            Native.FLEncoder_Reset(encoder);

            return body;
        }





        private void VerifyDB([@NotNull]Document document)
        {
            if (document.Database == null) {
                document.Database = this;
            } else if (document.Database != this) {
                throw new CouchbaseLiteException(C4ErrorCode.InvalidParameter,
                    CouchbaseLiteErrorMessage.DocumentAnotherDatabase);
            }
        }

        private void PurgeDocById(string id)
        {
            ThreadSafety.DoLockedBridge(err =>
            {
                return Native.c4db_purgeDoc(_c4db, id, err);
            });
        }

        private void FreeC4Db()
        {
            Native.c4db_release(_c4db);
            _c4db = null;
        }

        private void FreeC4DbObserver()
        {
            if (_obs != null) {
                Native.c4dbobs_free(_obs);
                _obsContext.Free();
            }
        }

        private void ClearUnsavedDocsAndFreeDocObservers()
        {
            //TODO _docObs might need to be refactored into an IDisposable class
            foreach (var obs in _docObs) {
                Native.c4docobs_free((C4DocumentObserver*) obs.Value.Item1);
                obs.Value.Item2.Free();
            }

            _docObs.Clear();
            //end of TODO comments

            if (_unsavedDocuments.Count > 0) {
                WriteLog.To.Database.W(Tag,
                    $"Closing database with {_unsavedDocuments.Count} such as {_unsavedDocuments.Any()}");
            }

            _unsavedDocuments.Clear();
        }


        private void CheckOpenAndNotClosing()
        {
            if (IsClosed || _isClosing) {
                throw new InvalidOperationException(CouchbaseLiteErrorMessage.DBClosed);
            }
        }

        #endregion

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Path?.GetHashCode() ?? 0;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (!(obj is Database other)) {
                return false;
            }

            return String.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public override string ToString() => $"DB[{Path}]";

        #region IDisposable

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);

            // Do this here because otherwise if a purge job runs there will
            // be a deadlock while the purge job waits for the lock that is held
            // by the disposal which is waiting for timer callbacks to finish
            var isClosed = ThreadSafety.DoLocked(() =>
            {
                if (IsClosed) {
                    return true;
                }

                if (!_isClosing) {
                    _isClosing = true;
                }

                return false;
            });

            if(isClosed) {
                return;
            }

            foreach (var q in ActiveStoppables) {
                q.Key.Stop();
            }

            while (!_closeCondition.Wait(TimeSpan.FromSeconds(5))) {
                WriteLog.To.Database.W(Tag, "Taking a while for active items to stop...");
            }

            ThreadSafety.DoLocked(() =>
            {
                Dispose(true);
            });
        }

        #endregion
    }
}

