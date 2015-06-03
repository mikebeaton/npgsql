﻿using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using NUnit.Framework.Constraints;

namespace Npgsql.Tests
{
    public class CopyTests : TestBase
    {
        [Test, Description("Exports data in binary format (raw mode) and then loads it back in")]
        public void RawBinaryRoundtrip()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);

            //var iterations = Conn.BufferSize / 10 + 100;
            //var iterations = Conn.BufferSize / 10 - 100;
            var iterations = 500;

            // Preload some data into the table
            using (var cmd = new NpgsqlCommand("INSERT INTO data (field_text, field_int4) VALUES (@p1, @p2)", Conn))
            {
                cmd.Parameters.AddWithValue("p1", NpgsqlDbType.Text, "HELLO");
                cmd.Parameters.AddWithValue("p2", NpgsqlDbType.Integer, 8);
                cmd.Prepare();
                for (var i = 0; i < iterations; i++)
                {
                    cmd.ExecuteNonQuery();
                }
            }

            var data = new byte[10000];
            int len;
            using (var inStream = Conn.BeginRawBinaryCopy("COPY data (field_text, field_int4) TO STDIN BINARY"))
            {
                StateAssertions(Conn);

                len = inStream.Read(data, 0, data.Length);
                Assert.That(len, Is.GreaterThan(Conn.BufferSize) & Is.LessThan(data.Length));
                Console.WriteLine("Exported binary dump, length=" + len);
            }

            ExecuteNonQuery("TRUNCATE data");

            using (var outStream = Conn.BeginRawBinaryCopy("COPY data (field_text, field_int4) FROM STDIN BINARY"))
            {
                StateAssertions(Conn);

                outStream.Write(data, 0, len);
            }

            Assert.That(ExecuteScalar("SELECT COUNT(*) FROM DATA"), Is.EqualTo(iterations));
        }

        [Test, Description("Disposes a raw binary stream in the middle of an export")]
        public void DisposeInMiddleOfRawBinaryExport()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);
            ExecuteNonQuery("INSERT INTO data (field_text, field_int4) VALUES ('HELLO', 8)", Conn);

            var data = new byte[3];
            using (var inStream = Conn.BeginRawBinaryCopy("COPY data (field_text, field_int4) TO STDIN BINARY"))
            {
                // Read some bytes
                var len = inStream.Read(data, 0, data.Length);
                Assert.That(len, Is.EqualTo(data.Length));
            }
            Assert.That(ExecuteScalar("SELECT 1"), Is.EqualTo(1));
        }

        [Test, Description("Disposes a raw binary stream in the middle of an import")]
        public void DisposeInMiddleOfRawBinaryImport()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);
            var inStream = Conn.BeginRawBinaryCopy("COPY data (field_text, field_int4) FROM STDIN BINARY");
            inStream.Write(NpgsqlRawCopyStream.BinarySignature, 0, NpgsqlRawCopyStream.BinarySignature.Length);
            Assert.That(() => inStream.Dispose(), Throws.Exception
                .TypeOf<NpgsqlException>()
                .With.Property("Code").EqualTo("22P04")
            );
            Assert.That(ExecuteScalar("SELECT 1"), Is.EqualTo(1));
        }

        [Test, Description("Cancels a binary write")]
        public void CancelRawBinaryImport()
        {
            using (var conn = new NpgsqlConnection(ConnectionString))
            {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                var garbage = new byte[] {1, 2, 3, 4};
                using (var s = conn.BeginRawBinaryCopy("COPY data (field_text, field_int4) FROM STDIN BINARY"))
                {
                    s.Write(garbage, 0, garbage.Length);
                    s.Cancel();
                }

                Assert.That(ExecuteScalar("SELECT COUNT(*) FROM data", conn), Is.EqualTo(0));
            }
        }

        [Test, Description("Roundtrips some data")]
        public void BinaryRoundtrip()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);
            var longString = new StringBuilder(Conn.BufferSize + 50).Append('a').ToString();

            using (var writer = Conn.BeginBinaryImport("COPY data (field_text, field_int2) FROM STDIN BINARY"))
            {
                StateAssertions(Conn);

                writer.StartRow();
                writer.Write("Hello");
                writer.Write(8, NpgsqlDbType.Smallint);

                writer.WriteRow("Something", (short)9);

                writer.StartRow();
                writer.Write(longString);
                writer.WriteNull();
            }

            using (var reader = Conn.BeginBinaryExport("COPY data (field_text, field_int2) TO STDIN BINARY"))
            {
                StateAssertions(Conn);

                Assert.That(reader.StartRow(), Is.EqualTo(2));
                Assert.That(reader.Read<string>(), Is.EqualTo("Hello"));
                Assert.That(reader.Read<int>(NpgsqlDbType.Smallint), Is.EqualTo(8));

                Assert.That(reader.StartRow(), Is.EqualTo(2));
                Assert.That(reader.IsNull, Is.False);
                Assert.That(reader.Read<string>(), Is.EqualTo("Something"));
                reader.Skip();

                Assert.That(reader.StartRow(), Is.EqualTo(2));
                Assert.That(reader.Read<string>(), Is.EqualTo(longString));
                Assert.That(reader.IsNull, Is.True);
                reader.Skip();

                Assert.That(reader.StartRow(), Is.EqualTo(-1));
            }
        }

        [Test]
        public void CancelBinaryImport()
        {
            using (var conn = new NpgsqlConnection(ConnectionString))
            {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                using (var writer = conn.BeginBinaryImport("COPY data (field_text, field_int4) FROM STDIN BINARY"))
                {
                    writer.StartRow();
                    writer.Write("Hello");
                    writer.Write(8);

                    writer.Cancel();
                }
                Assert.That(ExecuteScalar(@"SELECT COUNT(*) FROM data", conn), Is.EqualTo(0));
            }
        }

        #region Text In

        [Test]
        public void TextImport()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);
            const string line = "HELLO\t1\n";

            // Short write
            var writer = Conn.BeginTextImport("COPY data (field_text, field_int4) FROM STDIN");
            StateAssertions(Conn);
            writer.Write(line);
            writer.Close();
            Assert.That(ExecuteScalar(@"SELECT COUNT(*) FROM data WHERE field_int4=1"), Is.EqualTo(1));
            Assert.That(() => writer.Write(line), Throws.Exception.TypeOf<ObjectDisposedException>());
            ExecuteNonQuery("TRUNCATE data");

            // Long (multi-buffer) write
            var iterations = NpgsqlBuffer.MinimumBufferSize / line.Length + 100;
            writer = Conn.BeginTextImport("COPY data (field_text, field_int4) FROM STDIN");
            for (var i = 0; i < iterations; i++)
              writer.Write(line);
            writer.Close();
            Assert.That(ExecuteScalar(@"SELECT COUNT(*) FROM data WHERE field_int4=1"), Is.EqualTo(iterations));
        }

        [Test]
        public void CancelTextImport()
        {
            using (var conn = new NpgsqlConnection(ConnectionString))
            {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);

                var writer = (NpgsqlCopyTextWriter)conn.BeginTextImport("COPY data (field_text, field_int4) FROM STDIN");
                writer.Write("HELLO\t1\n");
                writer.Cancel();
                Assert.That(ExecuteScalar(@"SELECT COUNT(*) FROM data", conn), Is.EqualTo(0));
            }
        }

        [Test]
        public void TextImportEmpty()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);
            var writer = Conn.BeginTextImport("COPY data (field_text, field_int4) FROM STDIN");
            writer.Close();
            Assert.That(ExecuteScalar(@"SELECT COUNT(*) FROM data"), Is.EqualTo(0));
        }

        #endregion

        #region Text Out

        [Test]
        public void TextExport()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);
            var chars = new char[30];

            // Short read
            ExecuteNonQuery("INSERT INTO data (field_text, field_int4) VALUES ('HELLO', 1)");
            var reader = Conn.BeginTextExport("COPY data (field_text, field_int4) TO STDIN");
            StateAssertions(Conn);
            Assert.That(reader.Read(chars, 0, chars.Length), Is.EqualTo(8));
            Assert.That(new String(chars, 0, 8), Is.EqualTo("HELLO\t1\n"));
            Assert.That(reader.Read(chars, 0, chars.Length), Is.EqualTo(0));
            Assert.That(reader.Read(chars, 0, chars.Length), Is.EqualTo(0));
            reader.Close();
            Assert.That(() => reader.Read(chars, 0, chars.Length), Throws.Exception.TypeOf<ObjectDisposedException>());
            ExecuteNonQuery("TRUNCATE data");
        }

        [Test]
        public void DisposeInMiddleOfTextExport()
        {
            ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", Conn);
            ExecuteNonQuery("INSERT INTO data (field_text, field_int4) VALUES ('HELLO', 1)");
            var reader = Conn.BeginTextExport("COPY data (field_text, field_int4) TO STDIN");
            reader.Dispose();
            // Make sure the connection is stil OK
            Assert.That(ExecuteScalar("SELECT 1"), Is.EqualTo(1));
        }

        #endregion

        #region Other

        [Test, Description("Starts a transaction before a COPY, testing that prepended messages are handled well")]
        public void PrependedMessages()
        {
            Conn.BeginTransaction();
            TextImport();
        }

        [Test]
        public void UndefinedTable()
        {
            Assert.That(() => Conn.BeginBinaryImport("COPY undefined_table (field_text, field_int2) FROM STDIN BINARY"),
                Throws.Exception.TypeOf<NpgsqlException>().With.Property("Code").EqualTo("42P01"));
        }

        [Test]
        [IssueLink("https://github.com/npgsql/npgsql/issues/621")]
        public void CloseDuringCopy()
        {
            // TODO: Check no broken connections were returned to the pool
            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                conn.BeginBinaryImport("COPY data (field_text, field_int4) FROM STDIN BINARY");
            }

            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                conn.BeginBinaryExport("COPY data (field_text, field_int2) TO STDIN BINARY");
            }

            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                conn.BeginRawBinaryCopy("COPY data (field_text, field_int4) FROM STDIN BINARY");
            }

            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                conn.BeginRawBinaryCopy("COPY data (field_text, field_int4) TO STDIN BINARY");
            }

            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                conn.BeginTextImport("COPY data (field_text, field_int4) FROM STDIN");
            }

            using (var conn = new NpgsqlConnection(ConnectionString)) {
                conn.Open();
                ExecuteNonQuery("CREATE TEMP TABLE data (field_text TEXT, field_int2 SMALLINT, field_int4 INTEGER)", conn);
                conn.BeginTextExport("COPY data (field_text, field_int4) TO STDIN");
            }
        }

        /// <summary>
        /// Checks that the connector state is properly managed for COPY operations
        /// </summary>
        void StateAssertions(NpgsqlConnection conn)
        {
            Assert.That(conn.Connector.State, Is.EqualTo(ConnectorState.Copy));
            Assert.That(conn.State, Is.EqualTo(ConnectionState.Open));
            Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Open | ConnectionState.Fetching));
            Assert.That(() => ExecuteScalar("SELECT 1", conn), Throws.Exception.TypeOf<InvalidOperationException>());
        }

        #endregion

        public CopyTests(string backendVersion) : base(backendVersion) { }
    }
}
