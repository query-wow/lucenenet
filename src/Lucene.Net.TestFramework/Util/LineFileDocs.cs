using J2N;
using J2N.Threading.Atomic;
using Lucene.Net.Documents;
using Lucene.Net.Support.IO;
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Console = Lucene.Net.Util.SystemConsole;

namespace Lucene.Net.Util
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    /// <summary>
    /// Minimal port of benchmark's LneDocSource +
    /// DocMaker, so tests can enum docs from a line file created
    /// by benchmark's WriteLineDoc task
    /// </summary>
    public class LineFileDocs : IDisposable
    {
        private TextReader reader;
        private const int BUFFER_SIZE = 1 << 16; // 64K
        private const int CHAR_SIZE = sizeof(int);
        private const string TEMP_FILE_PREFIX = "lucene-linefiledocs-";
        private const string TEMP_FILE_SUFFIX = ".tmp";
        private readonly AtomicInt32 id = new AtomicInt32();
        private readonly string path;
        private readonly bool useDocValues;
        private readonly object syncLock = new object();
        private string tempFilePath;

        /// <summary>
        /// If forever is true, we rewind the file at EOF (repeat
        /// the docs over and over)
        /// </summary>
        public LineFileDocs(Random random, string path, bool useDocValues)
        {
            this.path = path;
            this.useDocValues = useDocValues;
            Open(random);
        }

        public LineFileDocs(Random random)
            : this(random, LuceneTestCase.TempLineDocsFile ?? LuceneTestCase.TestLineDocsFile, true)
        {
        }

        public LineFileDocs(Random random, bool useDocValues)
            : this(random, LuceneTestCase.TempLineDocsFile ?? LuceneTestCase.TestLineDocsFile, useDocValues)
        {
        }

        private void Close()
        {
            lock (syncLock)
            {
                if (reader != null)
                {
                    reader.Dispose();
                    reader = null;
                }
                if (!string.IsNullOrEmpty(tempFilePath))
                {
                    DeleteAsync(tempFilePath);
                    tempFilePath = null;
                }
            }
        }

        private static Task DeleteAsync(string path)
        {
            return Task.Run(() =>
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return; // Nothing to do

                try
                {
                    File.Delete(path);
                }
                catch { }
            });
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // LUCENENET specific: Implemented dispose pattern
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (syncLock)
                {
                    Close();
                    threadDocs?.Dispose();
                }
            }
        }

        private long RandomSeekPos(Random random, long size)
        {
            if (random == null || size <= 3L)
            {
                return 0L;
            }
            var result = (random.NextInt64() & long.MaxValue) % (size / 3);
            if (result > size - 7) result = size - 8;
            while (!(result % CHAR_SIZE == 0))
                result++;
            return result;
        }


        // LUCENENET specific - this was added to unzip our LineDocsFile to a specific folder
        // so tests can be run without the overhead of seeking within a MemoryStream
        private Stream PrepareGZipStream(Stream input)
        {
            using (var gzs = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false))
            {
                FileInfo tempFile = LuceneTestCase.CreateTempFile(TEMP_FILE_PREFIX, TEMP_FILE_SUFFIX);
                tempFilePath = tempFile.FullName;
                Stream result = new FileStream(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                gzs.CopyTo(result);
                // Use the decompressed stream now
                return new BufferedStream(result);
            }
        }

        private void Open(Random random)
        {
            lock (syncLock)
            {
                Stream @is = null;
                bool needSkip = true, isExternal = false;
                long size = 0L, seekTo = 0L;

                try
                {
                    // LUCENENET: We have embedded the default file, so if that filename is passed,
                    // open the local resource instead of an external file.
                    if (path == LuceneTestCase.DEFAULT_LINE_DOCS_FILE)
                        @is = this.GetType().FindAndGetManifestResourceStream(path);
                    else
                        isExternal = true;
                }
                catch (Exception)
                {
                    isExternal = true;
                }
                if (isExternal)
                {
                    // if its not in classpath, we load it as absolute filesystem path (e.g. Hudson's home dir)
                    FileInfo file = new FileInfo(path);
                    size = file.Length;
                    if (path.EndsWith(".gz", StringComparison.Ordinal))
                    {
                        // if it is a gzip file, we need to use InputStream and slowly skipTo:
                        @is = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    else
                    {
                        // optimized seek using RandomAccessFile:
                        seekTo = RandomSeekPos(random, size);
                        if (LuceneTestCase.Verbose)
                        {
                            Console.WriteLine($"TEST: LineFileDocs: file seek to fp={seekTo} on open");
                        }
                        @is = new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                        {
                            Position = seekTo
                        });
                        needSkip = false;
                    }
                }
                else
                {
                    // if the file comes from Classpath:
                    size = @is.Length;// available();
                }

                if (path.EndsWith(".gz", StringComparison.Ordinal))
                {
                    @is = PrepareGZipStream(@is);
                    // guestimate:
                    size = (long)(size * 2.8);
                }

                // If we only have an InputStream, we need to seek now,
                // but this seek is a scan, so very inefficient!!!
                if (needSkip)
                {
                    seekTo = RandomSeekPos(random, size);
                    if (LuceneTestCase.Verbose)
                    {
                        Console.WriteLine($"TEST: LineFileDocs: stream skip to fp={seekTo} on open");
                    }
                    @is.Position = seekTo;
                }

                // if we seeked somewhere, read until newline char
                if (seekTo > 0L)
                {
                    //int b;
                    //do
                    //{
                    //    b = @is.ReadByte();
                    //} while (b >= 0 && b != 13 && b != 10);
                    
                    SeekToNextLineBreakOrEnd(@is);
                }

                reader = new StreamReader(@is, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: BUFFER_SIZE);

                if (seekTo > 0L)
                {
                    // read one more line, to make sure we are not inside a Windows linebreak (\r\n):
                    reader.ReadLine();
                }
            }
        }

        private void SeekToNextLineBreakOrEnd(Stream @is)
        {
            int b, read, chunkSize = CHAR_SIZE * 1024;
            byte[] bytes = new byte[chunkSize];
            while (true)
            {
                read = @is.Read(bytes, 0, chunkSize);
                if (read == 0) return;

                for (int i = 0; i < read; i += CHAR_SIZE)
                {
                    b = BitConverter.ToInt32(bytes, i);
                    if (b == 13 || b == 10)
                    {
                        // Move the stream back from the current position to where we found the line break
                        @is.Seek(-(read - i), SeekOrigin.Current);
                        return;
                    }
                }
            }
        }

        public virtual void Reset(Random random)
        {
            lock (syncLock)
            {
                Close();
                Open(random);
                id.Value = 0;
            }
        }

        private const char SEP = '\t';

        private sealed class DocState
        {
            internal Document Doc { get; private set; }
            internal Field TitleTokenized { get; private set; }
            internal Field Title { get; private set; }
            internal Field TitleDV { get; private set; }
            internal Field Body { get; private set; }
            internal Field Id { get; private set; }
            internal Field Date { get; private set; }

            public DocState(bool useDocValues)
            {
                Doc = new Document();

                Title = new StringField("title", "", Field.Store.NO);
                Doc.Add(Title);

                FieldType ft = new FieldType(TextField.TYPE_STORED)
                {
                    StoreTermVectors = true,
                    StoreTermVectorOffsets = true,
                    StoreTermVectorPositions = true
                };

                TitleTokenized = new Field("titleTokenized", "", ft);
                Doc.Add(TitleTokenized);

                Body = new Field("body", "", ft);
                Doc.Add(Body);

                Id = new StringField("docid", "", Field.Store.YES);
                Doc.Add(Id);

                Date = new StringField("date", "", Field.Store.YES);
                Doc.Add(Date);

                if (useDocValues)
                {
                    TitleDV = new SortedDocValuesField("titleDV", new BytesRef());
                    Doc.Add(TitleDV);
                }
                else
                {
                    TitleDV = null;
                }
            }
        }

        private readonly ThreadLocal<DocState> threadDocs = new ThreadLocal<DocState>();

        /// <summary>
        /// Note: Document instance is re-used per-thread </summary>
        public virtual Document NextDoc()
        {
            string line;
            lock (syncLock)
            {
                line = reader.ReadLine();
                if (line == null)
                {
                    // Always rewind at end:
                    if (LuceneTestCase.Verbose)
                    {
                        Console.WriteLine("TEST: LineFileDocs: now rewind file...");
                    }
                    Close();
                    Open(null);
                    line = reader.ReadLine();
                }
            }

            DocState docState = threadDocs.Value;
            if (docState == null)
            {
                docState = new DocState(useDocValues);
                threadDocs.Value = docState;
            }

            int spot = line.IndexOf(SEP);
            if (spot == -1)
            {
                throw new Exception("line: [" + line + "] is in an invalid format !");
            }
            int spot2 = line.IndexOf(SEP, 1 + spot);
            if (spot2 == -1)
            {
                throw new Exception("line: [" + line + "] is in an invalid format !");
            }

            docState.Body.SetStringValue(line.Substring(1 + spot2, line.Length - (1 + spot2)));
            string title = line.Substring(0, spot);
            docState.Title.SetStringValue(title);
            if (docState.TitleDV != null)
            {
                docState.TitleDV.SetBytesValue(new BytesRef(title));
            }
            docState.TitleTokenized.SetStringValue(title);
            docState.Date.SetStringValue(line.Substring(1 + spot, spot2 - (1 + spot)));
            docState.Id.SetStringValue(Convert.ToString(id.GetAndIncrement(), CultureInfo.InvariantCulture));
            return docState.Doc;
        }

        internal static string MaybeCreateTempFile(bool removeAfterClass = true)
        {
            string result = null;
            Stream temp = null;
            if (LuceneTestCase.TestLineDocsFile == LuceneTestCase.DEFAULT_LINE_DOCS_FILE) // Always GZipped
            {
                temp = typeof(LineFileDocs).FindAndGetManifestResourceStream(LuceneTestCase.TestLineDocsFile);
            }
            else if (LuceneTestCase.TestLineDocsFile.EndsWith(".gz", StringComparison.Ordinal))
            {
                temp = new FileStream(LuceneTestCase.TestLineDocsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            if (null != temp)
            {
                var file = removeAfterClass
                    ? LuceneTestCase.CreateTempFile(TEMP_FILE_PREFIX, TEMP_FILE_SUFFIX)
                    : FileSupport.CreateTempFile(TEMP_FILE_PREFIX, TEMP_FILE_SUFFIX);
                result = file.FullName;
                using (var gzs = new GZipStream(temp, CompressionMode.Decompress, leaveOpen: false))
                using (Stream output = new FileStream(result, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    gzs.CopyTo(output);
                }
            }
            return result;
        }
    }
}