﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// A public class that take care of all engine data structure access - it´s basic implementation of a NoSql database
    /// Its isolated from complete solution - works on low level only (no linq, no poco... just BSON objects)
    /// [ThreadSafe]
    /// </summary>
    public partial class LiteEngine : IDisposable//: ILiteEngine
    {
        #region Services instances

        private readonly LockService _locker;

        private readonly DiskService _disk;

        private readonly WalIndexService _walIndex;

        private HeaderPage _header;

        // immutable settings
        private readonly EngineSettings _settings;

        private bool _shutdown = false;
        private bool _disposed = false;

        #region TempDB

        private LiteEngine _tempdb = null;
        private bool _disposeTempdb = false;

        /// <summary>
        /// Get/Set temporary engine database used to sort data
        /// </summary>
        public LiteEngine TempDB
        {
            get
            {
                if (_tempdb == null)
                {
                    _tempdb = new LiteEngine(new EngineSettings { Filename = ":temp:" });
                }

                return _tempdb;
            }
            set
            {
                if (_tempdb != null) throw LiteException.TempEngineAlreadyDefined();

                _tempdb = value;
                _disposeTempdb = false;
            }
        }

        #endregion

        /// <summary>
        /// Get database initialize settings
        /// </summary>
        internal EngineSettings Settings => _settings;

        #endregion

        #region Ctor

        /// <summary>
        /// Initialize LiteEngine using connection memory database
        /// </summary>
        public LiteEngine()
            : this(new EngineSettings())
        {
        }

        /// <summary>
        /// Initialize LiteEngine using connection string using key=value; parser
        /// </summary>
        public LiteEngine(string filename)
            : this (new EngineSettings { Filename = filename })
        {
        }

        /// <summary>
        /// Initialize LiteEngine using initial engine settings
        /// </summary>
        public LiteEngine(EngineSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _settings = settings;

            LOG("start initializing", "ENGINE");

            try
            {
                // initialize locker service (no dependency)
                _locker = new LockService(settings.Timeout, settings.ReadOnly);

                // initialize disk service (will create database if needed)
                _disk = new DiskService(settings);

                // read header page
                using (var reader = _disk.GetReader())
                {
                    // read page with no cache ref (has a own PageBuffer) - do not Release() support
                    var buffer = _disk.ReadFull(FileOrigin.Data).First();

                    _header = new HeaderPage(buffer);
                }

                // initialize wal-index service
                _walIndex = new WalIndexService(_disk, _locker);

                // if exists log file, restore wal index references (can update full _header instance)
                if (_disk.GetLength(FileOrigin.Log) > 0)
                {
                    _walIndex.RestoreIndex(ref _header);
                }

                // register system collections
                //** this.InitializeSystemCollections();

                // if fileVersion are less than current version, must upgrade datafile
                //** if (_header.FileVersion < HeaderPage.FILE_VERSION)
                //** {
                //**     this.Upgrade();
                //** }

                LOG("initialization completed", "ENGINE");
            }
            catch (Exception ex)
            {
                LOG(ex.Message, "ERROR");

                // explicit dispose (but do not run shutdown operation)
                this.Dispose(true);
                throw;
            }
        }

        #endregion

        public int PagesInUse => _disk.PagesInUse;
        public int QueueLength => _disk.Queue.Length;
        public void WaitQueue() => _disk.Queue.Wait();
        public int Checkpoint() => _walIndex.Checkpoint(_header, CheckpointMode.Incremental);

        //**        /// <summary>
        //**        /// Request a database checkpoint
        //**        /// </summary>
        //**        public int Checkpoint() => _walIndex.Checkpoint(_header, true);

        /// <summary>
        /// Shutdown database and do not accept any other access. Wait finish all transactions
        /// </summary>
        private void Shutdown()
        {
            // here all private instances are loaded
            if (_shutdown) return;

            // start shutdown operation
            _shutdown = true;

            LOG("shutting down", "ENGINE");

            // mark all transaction as shotdown status
            foreach (var trans in _transactions.Values)
            {
                trans.Shutdown();
            }

            if (_settings.CheckpointOnShutdown && _settings.ReadOnly == false)
            {
                _walIndex.Checkpoint(_header, CheckpointMode.Shutdown);
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            // this method can be called from Ctor, so many 
            // of this members can be null yet (even if are readonly). 

            if (_disposed) return;

            if (disposing)
            {
                // do not release _header page because header contains an own PageBuffer

                // close all disk connections
                _disk?.Dispose();

                // dispose lockers
                _locker?.Dispose();

                if (_disposeTempdb)
                {
                    _tempdb?.Dispose(disposing);
                }
            }

            _disposed = true;
        }

        /// <summary>
        /// Shutdown database
        /// - After dispose engine, no more new transaction
        /// - All transation will throw shutdown exception and do rollback
        /// - Wait for async write with full flush to disk
        /// - Do checkpoint (sync)
        /// - Dispose disks
        /// </summary>
        public void Dispose()
        {
            // shutdown all operations
            this.Shutdown();

            // dispose data file
            this.Dispose(true);

            GC.SuppressFinalize(this);

            LOG("engine disposed", "ENGINE");
        }

        ~LiteEngine()
        {
            this.Dispose(false);
        }
    }
}