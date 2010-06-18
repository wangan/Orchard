﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Orchard.Environment.Configuration;
using Orchard.FileSystems.AppData;
using Orchard.Indexing.Models;
using Orchard.Logging;
using System.Xml.Linq;
using Directory = Lucene.Net.Store.Directory;
using Version = Lucene.Net.Util.Version;

namespace Orchard.Indexing.Services {
    /// <summary>
    /// Represents the default implementation of an IIndexProvider, based on Lucene
    /// </summary>
    public class LuceneIndexProvider : IIndexProvider {
        private readonly IAppDataFolder _appDataFolder;
        private readonly ShellSettings _shellSettings;
        public static readonly Version LuceneVersion = Version.LUCENE_29;
        private readonly Analyzer _analyzer ;
        private readonly string _basePath;
        public static readonly DateTime DefaultMinDateTime = new DateTime(1980, 1, 1);
        public static readonly string Settings = "Settings";
        public static readonly string LastIndexUtc = "LastIndexedUtc";

        public ILogger Logger { get; set; }

        public LuceneIndexProvider(IAppDataFolder appDataFolder, ShellSettings shellSettings) {
            _appDataFolder = appDataFolder;
            _shellSettings = shellSettings;
            _analyzer = CreateAnalyzer();

            // TODO: (sebros) Find a common way to get where tenant's specific files should go. "Sites/Tenant" is hard coded in multiple places
            _basePath = _appDataFolder.Combine("Sites", _shellSettings.Name, "Indexes");

            Logger = NullLogger.Instance;

            // Ensures the directory exists
            EnsureDirectoryExists();
        }

        public static Analyzer CreateAnalyzer() {
            // StandardAnalyzer does lower-case and stop-word filtering. It also removes punctuation
            return new StandardAnalyzer(LuceneVersion);
        }

        private void EnsureDirectoryExists() {
            var directory = new DirectoryInfo(_appDataFolder.MapPath(_basePath));
            if(!directory.Exists) {
                directory.Create();
            }
        }

        protected virtual Directory GetDirectory(string indexName) {
            var directoryInfo = new DirectoryInfo(_appDataFolder.MapPath(_appDataFolder.Combine(_basePath, indexName)));
            return FSDirectory.Open(directoryInfo);
        }

        private static Document CreateDocument(LuceneDocumentIndex indexDocument) {
            var doc = new Document();

            indexDocument.PrepareForIndexing();
            foreach(var field in indexDocument.Fields) {
                doc.Add(field);
            }
            return doc;
        }

        public bool Exists(string indexName) {
            return new DirectoryInfo(_appDataFolder.MapPath(_appDataFolder.Combine(_basePath, indexName))).Exists;
        }

        public bool IsEmpty(string indexName) {
            if ( !Exists(indexName) ) {
                return true;
            }

            var reader = IndexReader.Open(GetDirectory(indexName), true);

            try {
                return reader.NumDocs() == 0;
            }
            finally {
                reader.Close();
            }
        }

        public int NumDocs(string indexName) {
            if ( !Exists(indexName) ) {
                return 0;
            }

            var reader = IndexReader.Open(GetDirectory(indexName), true);

            try {
                return reader.NumDocs();
            }
            finally {
                reader.Close();
            }
        }

        public void CreateIndex(string indexName) {
            var writer = new IndexWriter(GetDirectory(indexName), _analyzer, true, IndexWriter.MaxFieldLength.UNLIMITED);
            writer.Close();

            Logger.Information("Index [{0}] created", indexName);
        }

        public void DeleteIndex(string indexName) {
            new DirectoryInfo(_appDataFolder.MapPath(_appDataFolder.Combine(_basePath, indexName)))
                .Delete(true);

            var settingsFileName = GetSettingsFileName(indexName);
            if(File.Exists(settingsFileName)) {
                File.Delete(settingsFileName);
            }
        }

        public void Store(string indexName, IDocumentIndex indexDocument) {
            Store(indexName, new [] { (LuceneDocumentIndex)indexDocument });
        }

        public void Store(string indexName, IEnumerable<IDocumentIndex> indexDocuments) {
            Store(indexName, indexDocuments.Cast<LuceneDocumentIndex>());
        }

        public void Store(string indexName, IEnumerable<LuceneDocumentIndex> indexDocuments) {
            if(indexDocuments.AsQueryable().Count() == 0) {
                return;
            }

            var writer = new IndexWriter(GetDirectory(indexName), _analyzer, false, IndexWriter.MaxFieldLength.UNLIMITED);
            LuceneDocumentIndex current = null;

            try {
                foreach ( var indexDocument in indexDocuments ) {
                    current = indexDocument;
                    var doc = CreateDocument(indexDocument);
                    writer.AddDocument(doc);
                    Logger.Debug("Document [{0}] indexed", indexDocument.ContentItemId);
                }
            }
            catch ( Exception ex ) {
                Logger.Error(ex, "An unexpected error occured while add the document [{0}] from the index [{1}].", current.ContentItemId, indexName);
            }
            finally {
                writer.Optimize();
                writer.Close();
            }
        }

        public void Delete(string indexName, int documentId) {
            Delete(indexName, new[] { documentId });
        }

        public void Delete(string indexName, IEnumerable<int> documentIds) {
            if ( documentIds.AsQueryable().Count() == 0 ) {
                return;
            }
            
            var reader = IndexReader.Open(GetDirectory(indexName), false);

            try {
                foreach (var id in documentIds) {
                    try {
                        var term = new Term("id", id.ToString());
                        if (reader.DeleteDocuments(term) != 0) {
                            Logger.Error("The document [{0}] could not be removed from the index [{1}]", id, indexName);
                        }
                        else {
                            Logger.Debug("Document [{0}] removed from index", id);
                        }
                    }
                    catch (Exception ex) {
                        Logger.Error(ex, "An unexpected error occured while removing the document [{0}] from the index [{1}].", id, indexName);
                    }
                }
            }
            finally {
                reader.Close();
            }
        }

        public IDocumentIndex New(int documentId) {
            return new LuceneDocumentIndex(documentId);
        }

        public ISearchBuilder CreateSearchBuilder(string indexName) {
            return new LuceneSearchBuilder(GetDirectory(indexName));
        }

        private string GetSettingsFileName(string indexName) {
            return _appDataFolder.MapPath(_appDataFolder.Combine(_basePath, indexName + ".settings.xml"));
        }

        public DateTime GetLastIndexUtc(string indexName) {
            var settingsFileName = GetSettingsFileName(indexName);

            return File.Exists(settingsFileName) 
                ? DateTime.Parse(XDocument.Load(settingsFileName).Descendants(LastIndexUtc).First().Value)
                : DefaultMinDateTime;
        }

        public void SetLastIndexUtc(string indexName, DateTime lastIndexUtc) {
            if ( lastIndexUtc < DefaultMinDateTime ) {
                lastIndexUtc = DefaultMinDateTime;
            }

            XDocument doc;
            var settingsFileName = GetSettingsFileName(indexName);
            if ( !File.Exists(settingsFileName) ) {
                EnsureDirectoryExists();
                doc = new XDocument(
                        new XElement(Settings,
                            new XElement(LastIndexUtc, lastIndexUtc.ToString("s"))));
            }
            else {
                doc = XDocument.Load(settingsFileName);
                doc.Element(Settings).Element(LastIndexUtc).Value = lastIndexUtc.ToString("s");
            }

            doc.Save(settingsFileName);
        }

        public string[] GetFields(string indexName) {
            if ( !Exists(indexName) ) {
                return new string[0];
            }

            var reader = IndexReader.Open(GetDirectory(indexName), true);

            try {
                return reader.GetFieldNames(IndexReader.FieldOption.ALL).ToArray();
            }
            finally {
                reader.Close();
            }
        }
    }
}
