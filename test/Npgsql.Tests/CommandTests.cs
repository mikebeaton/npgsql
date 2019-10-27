using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NpgsqlTypes;
using NUnit.Framework;

namespace Npgsql.Tests
{
    public class CommandTests : TestBase
    {
        #region Multiple Statements in a Command

        /// <summary>
        /// Tests various configurations of queries and non-queries within a multiquery
        /// </summary>
        [Test]
        [TestCase(new[] { true }, TestName = "SingleQuery")]
        [TestCase(new[] { false }, TestName = "SingleNonQuery")]
        [TestCase(new[] { true, true }, TestName = "TwoQueries")]
        [TestCase(new[] { false, false }, TestName = "TwoNonQueries")]
        [TestCase(new[] { false, true }, TestName = "NonQueryQuery")]
        [TestCase(new[] { true, false }, TestName = "QueryNonQuery")]
        public void MultipleStatements(bool[] queries)
        {
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TEMP TABLE data (name TEXT)");
                var sb = new StringBuilder();
                foreach (var query in queries)
                    sb.Append(query ? "SELECT 1;" : "UPDATE data SET name='yo' WHERE 1=0;");
                var sql = sb.ToString();
                foreach (var prepare in new[] {false, true})
                {
                    using (var cmd = new NpgsqlCommand(sql, conn))
                    {
                        if (prepare)
                            cmd.Prepare();
                        using (var reader = cmd.ExecuteReader())
                        {
                            var numResultSets = queries.Count(q => q);
                            for (var i = 0; i < numResultSets; i++)
                            {
                                Assert.That(reader.Read(), Is.True);
                                Assert.That(reader[0], Is.EqualTo(1));
                                Assert.That(reader.NextResult(), Is.EqualTo(i != numResultSets - 1));
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void MultipleStatementsWithParameters([Values(PrepareOrNot.NotPrepared, PrepareOrNot.Prepared)] PrepareOrNot prepare)
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = new NpgsqlCommand("SELECT @p1; SELECT @p2", conn))
                {
                    var p1 = new NpgsqlParameter("p1", NpgsqlDbType.Integer);
                    var p2 = new NpgsqlParameter("p2", NpgsqlDbType.Text);
                    cmd.Parameters.Add(p1);
                    cmd.Parameters.Add(p2);
                    if (prepare == PrepareOrNot.Prepared)
                        cmd.Prepare();
                    p1.Value = 8;
                    p2.Value = "foo";
                    using (var reader = cmd.ExecuteReader())
                    {
                        Assert.That(reader.Read(), Is.True);
                        Assert.That(reader.GetInt32(0), Is.EqualTo(8));
                        Assert.That(reader.NextResult(), Is.True);
                        Assert.That(reader.Read(), Is.True);
                        Assert.That(reader.GetString(0), Is.EqualTo("foo"));
                        Assert.That(reader.NextResult(), Is.False);
                    }
                }
            }
        }

        [Test]
        public void MultipleStatementsSingleRow([Values(PrepareOrNot.NotPrepared, PrepareOrNot.Prepared)] PrepareOrNot prepare)
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = new NpgsqlCommand("SELECT 1; SELECT 2", conn))
                {
                    if (prepare == PrepareOrNot.Prepared)
                        cmd.Prepare();
                    using (var reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        Assert.That(reader.Read(), Is.True);
                        Assert.That(reader.GetInt32(0), Is.EqualTo(1));
                        Assert.That(reader.Read(), Is.False);
                        Assert.That(reader.NextResult(), Is.False);
                    }
                }
            }
        }

        [Test, Description("Makes sure a later command can depend on an earlier one")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/641")]
        public void MultipleStatementsWithDependencies()
        {
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TABLE pg_temp.data (a INT); INSERT INTO pg_temp.data (a) VALUES (8)");
                Assert.That(conn.ExecuteScalar("SELECT * FROM pg_temp.data"), Is.EqualTo(8));
            }
        }

        [Test, Description("Forces async write mode when the first statement in a multi-statement command is big")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/641")]
        public void MultipleStatementsLargeFirstCommand()
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand($"SELECT repeat('X', {conn.Settings.WriteBufferSize}); SELECT @p", conn))
            {
                var expected1 = new string('X', conn.Settings.WriteBufferSize);
                var expected2 = new string('Y', conn.Settings.WriteBufferSize);
                cmd.Parameters.AddWithValue("p", expected2);
                using (var reader = cmd.ExecuteReader())
                {
                    reader.Read();
                    Assert.That(reader.GetString(0), Is.EqualTo(expected1));
                    reader.NextResult();
                    reader.Read();
                    Assert.That(reader.GetString(0), Is.EqualTo(expected2));
                }
            }
        }

        #endregion

        #region Prepare() corner cases

        [Test]
        public void PrepareMultipleCommandsWithParameters()
        {
            using (var conn = OpenConnection())
            {
                using (var cmd1 = new NpgsqlCommand("SELECT @p1;", conn))
                using (var cmd2 = new NpgsqlCommand("SELECT @p1; SELECT @p2;", conn))
                {
                    var p1 = new NpgsqlParameter("p1", NpgsqlDbType.Integer);
                    var p21 = new NpgsqlParameter("p1", NpgsqlDbType.Text);
                    var p22 = new NpgsqlParameter("p2", NpgsqlDbType.Text);
                    cmd1.Parameters.Add(p1);
                    cmd2.Parameters.Add(p21);
                    cmd2.Parameters.Add(p22);
                    cmd1.Prepare();
                    cmd2.Prepare();
                    p1.Value = 8;
                    p21.Value = "foo";
                    p22.Value = "bar";
                    using (var reader1 = cmd1.ExecuteReader())
                    {
                        Assert.That(reader1.Read(), Is.True);
                        Assert.That(reader1.GetInt32(0), Is.EqualTo(8));
                    }
                    using (var reader2 = cmd2.ExecuteReader())
                    {
                        Assert.That(reader2.Read(), Is.True);
                        Assert.That(reader2.GetString(0), Is.EqualTo("foo"));
                        Assert.That(reader2.NextResult(), Is.True);
                        Assert.That(reader2.Read(), Is.True);
                        Assert.That(reader2.GetString(0), Is.EqualTo("bar"));
                    }
                }
            }
        }



        #endregion

        #region Timeout

        [Test, Description("Checks that CommandTimeout gets enforced as a socket timeout")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/327")]
        [Timeout(10000)]
        public void Timeout()
        {
            // Mono throws a socket exception with WouldBlock instead of TimedOut (see #1330)
            var isMono = Type.GetType("Mono.Runtime") != null;
            using (var conn = OpenConnection(ConnectionString + ";CommandTimeout=1"))
            using (var cmd = CreateSleepCommand(conn, 10))
            {
                Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception
                    .TypeOf<NpgsqlException>()
                    .With.InnerException.TypeOf<IOException>()
                    .With.InnerException.InnerException.TypeOf<SocketException>()
                    .With.InnerException.InnerException.Property(nameof(SocketException.SocketErrorCode)).EqualTo(isMono ? SocketError.WouldBlock : SocketError.TimedOut)
                    );
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Broken));
            }
        }

        [Test]
        public void TimeoutFromConnectionString()
        {
            Assert.That(NpgsqlConnector.MinimumInternalCommandTimeout, Is.Not.EqualTo(NpgsqlCommand.DefaultTimeout));
            var timeout = NpgsqlConnector.MinimumInternalCommandTimeout;
            var connString = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                CommandTimeout = timeout
            }.ToString();
            using (var conn = new NpgsqlConnection(connString))
            {
                var command = new NpgsqlCommand("SELECT 1", conn);
                conn.Open();
                Assert.That(command.CommandTimeout, Is.EqualTo(timeout));
                command.CommandTimeout = 10;
                command.ExecuteScalar();
                Assert.That(command.CommandTimeout, Is.EqualTo(10));
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/395")]
        public void TimeoutSwitchConnection()
        {
            using (var conn = new NpgsqlConnection(ConnectionString))
            {
                if (conn.CommandTimeout >= 100 && conn.CommandTimeout < 105)
                    TestUtil.IgnoreExceptOnBuildServer("Bad default command timeout");
            }

            using (var c1 = OpenConnection(ConnectionString + ";CommandTimeout=100"))
            {
                using (var cmd = c1.CreateCommand())
                {
                    Assert.That(cmd.CommandTimeout, Is.EqualTo(100));
                    using (var c2 = new NpgsqlConnection(ConnectionString + ";CommandTimeout=101"))
                    {
                        cmd.Connection = c2;
                        Assert.That(cmd.CommandTimeout, Is.EqualTo(101));
                    }
                    cmd.CommandTimeout = 102;
                    using (var c2 = new NpgsqlConnection(ConnectionString + ";CommandTimeout=101"))
                    {
                        cmd.Connection = c2;
                        Assert.That(cmd.CommandTimeout, Is.EqualTo(102));
                    }
                }
            }
        }

        #endregion

        #region Cancel

        [Test, Description("Basic cancellation scenario")]
        [Timeout(6000)]
        public void Cancel()
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = CreateSleepCommand(conn, 5))
                {
                    Task.Factory.StartNew(() =>
                    {
                        Thread.Sleep(300);
                        cmd.Cancel();
                    });
                    Assert.That(() => cmd.ExecuteNonQuery(), Throws
                        .TypeOf<PostgresException>()
                        .With.Property(nameof(PostgresException.SqlState)).EqualTo("57014")
                    );
                }
            }
        }

        [Test, Description("Cancels an async query with the cancellation token")]
        [Ignore("Flaky on CoreCLR")]
        public void CancelAsync()
        {
            var cancellationSource = new CancellationTokenSource();
            using (var conn = OpenConnection())
            using (var cmd = CreateSleepCommand(conn))
            {
                // ReSharper disable once MethodSupportsCancellation
                Task.Factory.StartNew(() =>
                {
                    Thread.Sleep(300);
                    cancellationSource.Cancel();
                });
                var t = cmd.ExecuteNonQueryAsync(cancellationSource.Token);
                Assert.That(async () => await t.ConfigureAwait(false), Throws.Exception.TypeOf<NpgsqlException>());

                // Since cancellation triggers an NpgsqlException and not a TaskCanceledException, the task's state
                // is Faulted and not Canceled. This isn't amazing, but we have to choose between this and the
                // principle of always raising server/network errors as NpgsqlException for easy catching.
                Assert.That(t.IsFaulted);
                Assert.That(conn.FullState, Is.EqualTo(ConnectionState.Broken));
            }
        }

        [Test, Description("Check that cancel only affects the command on which its was invoked")]
        [Timeout(3000)]
        public void CancelCrossCommand()
        {
            using (var conn = OpenConnection())
            {
                using (var cmd1 = CreateSleepCommand(conn, 2))
                using (var cmd2 = new NpgsqlCommand("SELECT 1", conn))
                {
                    var cancelTask = Task.Factory.StartNew(() =>
                    {
                        Thread.Sleep(300);
                        cmd2.Cancel();
                    });
                    Assert.That(() => cmd1.ExecuteNonQuery(), Throws.Nothing);
                    cancelTask.Wait();
                }
            }
        }

        #endregion

        #region Cursors

        [Test]
        public void CursorStatement()
        {
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TEMP TABLE data (name TEXT)");
                using (var t = conn.BeginTransaction())
                {
                    for (var x = 0; x < 5; x++)
                        conn.ExecuteNonQuery(@"INSERT INTO data (name) VALUES ('X')");

                    var i = 0;
                    var command = new NpgsqlCommand("DECLARE TE CURSOR FOR SELECT * FROM DATA", conn);
                    command.ExecuteNonQuery();
                    command.CommandText = "FETCH FORWARD 3 IN TE";
                    var dr = command.ExecuteReader();

                    while (dr.Read())
                        i++;
                    Assert.AreEqual(3, i);
                    dr.Close();

                    i = 0;
                    command.CommandText = "FETCH BACKWARD 1 IN TE";
                    var dr2 = command.ExecuteReader();
                    while (dr2.Read())
                        i++;
                    Assert.AreEqual(1, i);
                    dr2.Close();

                    command.CommandText = "close te;";
                    command.ExecuteNonQuery();
                }
            }
        }

        [Test]
        public void CursorMoveRecordsAffected()
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var command = new NpgsqlCommand("DECLARE curs CURSOR FOR SELECT * FROM (VALUES (1), (2), (3)) as t", connection);
                command.ExecuteNonQuery();
                command.CommandText = "MOVE FORWARD ALL IN curs";
                var count = command.ExecuteNonQuery();
                Assert.AreEqual(3, count);
            }
        }

        #endregion

        #region Cursor dereferencing

        const string defineTestMultCurFunc = @"CREATE OR REPLACE FUNCTION testmultcurfunc() RETURNS SETOF refcursor AS 'DECLARE ref1 refcursor; ref2 refcursor; BEGIN OPEN ref1 FOR SELECT 1; RETURN NEXT ref1; OPEN ref2 FOR SELECT 2; RETURN next ref2; RETURN; END;' LANGUAGE 'plpgsql';";

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task MultipleRefCursorSupport(bool async)
        {
            // this is the original npgsql v2 deref test (but now in sync and async)
            // https://github.com/npgsql/npgsql/issues/438#issuecomment-68093947
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { DereferenceCursors = true };
            using (var conn = OpenConnection(csb))
            {
                if (async) await conn.ExecuteNonQueryAsync(defineTestMultCurFunc);
                else conn.ExecuteNonQuery(defineTestMultCurFunc);
                using (conn.BeginTransaction())
                {
                    var command = new NpgsqlCommand("testmultcurfunc", conn);
                    command.CommandType = CommandType.StoredProcedure;
                    using (var dr = async ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                    {
                        if (async) await dr.ReadAsync(); else dr.Read();
                        var one = dr.GetInt32(0);
                        if (async) await dr.NextResultAsync(); else dr.NextResult();
                        if (async) await dr.ReadAsync(); else dr.Read();
                        var two = dr.GetInt32(0);
                        Assert.AreEqual(1, one);
                        Assert.AreEqual(2, two);
                    }
                }
            }
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task CursorNByOne(bool async)
        {
            // the multi-ref cursor above tests 1 x n, this tests n x 1
            const string defineCursorNByOne =
@"CREATE OR REPLACE FUNCTION public.cursornbyone(
    OUT mycursor1 refcursor,
    OUT mycursor2 refcursor)
  RETURNS SETOF record AS
$BODY$
DECLARE ref1 refcursor; ref2 refcursor;
BEGIN
    OPEN ref1 FOR SELECT 'my' as a, 7 as b;
    OPEN ref2 FOR SELECT 5 as c, 'test' as d;
    
    RETURN QUERY
    SELECT ref1, ref2;
END;
$BODY$
  LANGUAGE plpgsql
";
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { DereferenceCursors = true };
            using (var conn = OpenConnection(csb))
            {
                if (async) await conn.ExecuteNonQueryAsync(defineCursorNByOne);
                else conn.ExecuteNonQuery(defineCursorNByOne);
                using (conn.BeginTransaction())
                {
                    var command = new NpgsqlCommand("cursornbyone", conn);
                    command.CommandType = CommandType.StoredProcedure;
                    using (var dr = async ? await command.ExecuteReaderAsync() : command.ExecuteReader())
                    {
                        if (async) await dr.ReadAsync(); else dr.Read();
                        var a = dr.GetString(0);
                        var b = dr.GetInt32(1);
                        if (async) await dr.NextResultAsync(); else dr.NextResult();
                        if (async) await dr.ReadAsync(); else dr.Read();
                        var c = dr.GetInt32(0);
                        var d = dr.GetString(1);
                        Assert.That(a, Is.EqualTo("my"));
                        Assert.That(b, Is.EqualTo(7));
                        Assert.That(c, Is.EqualTo(5));
                        Assert.That(d, Is.EqualTo("test"));
                    }
                }
            }
        }

        [Test]
        public void CursorDereferencingOffByDefault()
        {
            // step through testmultcurfunc resultset as it is without dereferencing
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery(defineTestMultCurFunc);
                using (conn.BeginTransaction())
                {
                    var command = new NpgsqlCommand("testmultcurfunc", conn);
                    command.CommandType = CommandType.StoredProcedure;
                    using (var dr = command.ExecuteReader())
                    {
                        Assert.That(dr.GetDataTypeName(0), Is.EqualTo("refcursor"));
                        dr.Read(); // first cursor
                        Assert.That(dr.Read(), Is.True); // second cursor
                        Assert.That(dr.Read(), Is.False);
                    }
                }
            }
        }

        const string defineCTest =
@"CREATE OR REPLACE FUNCTION public.ctest()
  RETURNS SETOF refcursor AS
$BODY$
DECLARE ref1 refcursor; ref2 refcursor;
BEGIN
OPEN ref1 FOR SELECT a, 'hello' FROM generate_series(1, 8) a;
RETURN NEXT ref1;
OPEN ref2 FOR SELECT b, 'goodbye' FROM generate_series(100, 108) b;
RETURN next ref2;
RETURN;
END;
$BODY$
  LANGUAGE plpgsql
";

        [Test]
        [TestCase(null, false)]
        [TestCase(null, true)]
        [TestCase(-1, false)]
        [TestCase(-1, true)]
        [TestCase(3, false)]
        [TestCase(3, true)]
        public async Task ExpectedFetchStatements(int? fetchSize, bool async)
        {
            var csb = fetchSize == null ?
                new NpgsqlConnectionStringBuilder(ConnectionString) { DereferenceCursors = true } :
                new NpgsqlConnectionStringBuilder(ConnectionString) { DereferenceCursors = true, DereferenceFetchSize = (int)fetchSize };
            using (var conn = OpenConnection(csb))
            {
                if (async) await conn.ExecuteNonQueryAsync(defineCTest);
                else conn.ExecuteNonQuery(defineCTest);
                using (conn.BeginTransaction())
                {
                    var command = new NpgsqlCommand("ctest", conn);
                    command.CommandType = CommandType.StoredProcedure;
                    var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    using (var dr = reader)
                    {
                        var set = 0;
                        do
                        {
                            var row = 0;
                            while (async ? await dr.ReadAsync() : dr.Read())
                            {
                                var n = dr.GetInt32(0);
                                var s = dr.GetString(1);
                                Assert.That(n, Is.EqualTo((set == 0 ? 1 : 100) + row));
                                Assert.That(s, Is.EqualTo(set == 0 ? "hello" : "goodbye"));
                                row++;
                            }
                            Assert.That(row, Is.EqualTo(8 + set));
                            set++;
                        }
                        while (async ? await dr.NextResultAsync() : dr.NextResult());
                        Assert.That(set, Is.EqualTo(2));
                    }
                    switch (fetchSize)
                    {
                        case -1:
                            Assert.That(reader.Statements.Count, Is.EqualTo(4));
                            Assert.That(reader.Statements[0].SQL.StartsWith("FETCH ALL FROM"));
                            break;

                        case null:
                            Assert.That(reader.Statements.Count, Is.EqualTo(4));
                            Assert.That(reader.Statements[0].SQL.StartsWith("FETCH 10000 FROM"));
                            break;

                        case 3:
                            Assert.That(reader.Statements.Count, Is.EqualTo(9));
                            Assert.That(reader.Statements[0].SQL.StartsWith("FETCH 3 FROM"));
                            Assert.That(reader.Statements[3].SQL.StartsWith("CLOSE"));
                            Assert.That(reader.Statements[8].SQL.StartsWith("CLOSE"));
                            break;
                        default:
                            throw new InvalidOperationException($"Illegal {nameof(fetchSize)}");
                    }
                }
            }
        }

        [Test]
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task ExpectedFetchStatementsBreakEarly(bool async, bool exception)
        {
            var csb = new NpgsqlConnectionStringBuilder(ConnectionString) { DereferenceCursors = true, DereferenceFetchSize = 3 };
            using (var conn = OpenConnection(csb))
            {
                if (async) await conn.ExecuteNonQueryAsync(defineCTest);
                else conn.ExecuteNonQuery(defineCTest);
                using (conn.BeginTransaction())
                {
                    var command = new NpgsqlCommand("ctest", conn);
                    command.CommandType = CommandType.StoredProcedure;
                    var reader = async ? await command.ExecuteReaderAsync() : command.ExecuteReader();
                    try
                    {
                        using (var dr = reader)
                        {
                            var set = 0;
                            do
                            {
                                var row = 0;
                                while (async ? await dr.ReadAsync() : dr.Read())
                                {
                                    row++;
                                    if (row == (set + 1) * 4)
                                    {
                                        if (exception) throw new ApplicationException();
                                        else break;
                                    }
                                }
                                set++;
                            }
                            while (async ? await dr.NextResultAsync() : dr.NextResult());
                        }
                    }
                    catch (ApplicationException)
                    {
                        // ignore
                    }
                    Assert.That(reader.Statements.Count, Is.EqualTo(exception ? 3 : 7));
                    Assert.That(reader.Statements[0].SQL.StartsWith("FETCH 3 FROM"));
                    Assert.That(reader.Statements[2].SQL.StartsWith("CLOSE"));
                    if (!exception) Assert.That(reader.Statements[6].SQL.StartsWith("CLOSE"));
                }
            }
        }

        [Test]
        [TestCase(false)]
        [TestCase(true)]
        public void CancelEarlyClosesCleanly(bool async)
        {
            // Actual Close is called, RowsAffected is correct, ReaderClosed is called...
            throw new NotImplementedException();
        }

        [Test]
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task DereferencingRespondsToCancellationAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            using (var cts = new CancellationTokenSource())
            {
                ///
                cts.Cancel();
            }
        }

#if DEBUG
        [Test]
        public void TestWrappingIsOff()
        {
            // We want this test to fail when test wrapping is enabled, so that we definitely notice when it is switched on
            Assert.That(NpgsqlWrappingReader.TestWrapEverything, Is.False);
        }
#endif

#endregion

#region CommandBehavior.CloseConnection

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/693")]
        public void CloseConnection()
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = new NpgsqlCommand("SELECT 1", conn))
                using (var reader = cmd.ExecuteReader(CommandBehavior.CloseConnection))
                    while (reader.Read()) {}
                Assert.That(conn.State, Is.EqualTo(ConnectionState.Closed));
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/1194")]
        public void CloseConnectionWithOpenReaderWithCloseConnection()
        {
            using (var conn = OpenConnection())
            {
                var cmd = new NpgsqlCommand("SELECT 1", conn);
                var reader = cmd.ExecuteReader(CommandBehavior.CloseConnection);
                var wasClosed = false;
                reader.ReaderClosed += (sender, args) => { wasClosed = true; };
                conn.Close();
                Assert.That(wasClosed, Is.True);
            }
        }

        [Test]
        public void CloseConnectionWithException()
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = new NpgsqlCommand("SE", conn))
                    Assert.That(() => cmd.ExecuteReader(CommandBehavior.CloseConnection), Throws.Exception.TypeOf<PostgresException>());
                Assert.That(conn.State, Is.EqualTo(ConnectionState.Closed));
            }
        }

#endregion

        [Test]
        public void SingleRow([Values(PrepareOrNot.NotPrepared, PrepareOrNot.Prepared)] PrepareOrNot prepare)
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = new NpgsqlCommand("SELECT 1, 2 UNION SELECT 3, 4", conn))
                {
                    if (prepare == PrepareOrNot.Prepared)
                        cmd.Prepare();

                    using (var reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
                    {
                        Assert.That(() => reader.GetInt32(0), Throws.Exception.TypeOf<InvalidOperationException>());
                        Assert.That(reader.Read(), Is.True);
                        Assert.That(reader.GetInt32(0), Is.EqualTo(1));
                        Assert.That(reader.Read(), Is.False);
                    }
                }
            }
        }

        [Test, Description("Makes sure writing an unset parameter isn't allowed")]
        public void ParameterUnset()
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = new NpgsqlCommand("SELECT @p", conn))
                {
                    cmd.Parameters.Add(new NpgsqlParameter("@p", NpgsqlDbType.Integer));
                    Assert.That(() => cmd.ExecuteScalar(), Throws.Exception.TypeOf<InvalidCastException>());
                }
            }
        }

        [Test]
        public void ParametersGetName()
        {
            var command = new NpgsqlCommand();

            // Add parameters.
            command.Parameters.Add(new NpgsqlParameter(":Parameter1", DbType.Boolean));
            command.Parameters.Add(new NpgsqlParameter(":Parameter2", DbType.Int32));
            command.Parameters.Add(new NpgsqlParameter(":Parameter3", DbType.DateTime));
            command.Parameters.Add(new NpgsqlParameter("Parameter4", DbType.DateTime));

            var idbPrmtr = command.Parameters["Parameter1"];
            Assert.IsNotNull(idbPrmtr);
            command.Parameters[0].Value = 1;

            // Get by indexers.

            Assert.AreEqual(":Parameter1", command.Parameters["Parameter1"].ParameterName);
            Assert.AreEqual(":Parameter2", command.Parameters["Parameter2"].ParameterName);
            Assert.AreEqual(":Parameter3", command.Parameters["Parameter3"].ParameterName);
            Assert.AreEqual("Parameter4", command.Parameters["Parameter4"].ParameterName); //Should this work?

            Assert.AreEqual(":Parameter1", command.Parameters[0].ParameterName);
            Assert.AreEqual(":Parameter2", command.Parameters[1].ParameterName);
            Assert.AreEqual(":Parameter3", command.Parameters[2].ParameterName);
            Assert.AreEqual("Parameter4", command.Parameters[3].ParameterName);
        }

        [Test]
        public void SameParamMultipleTimes()
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT @p1, @p1", conn))
            {
                cmd.Parameters.AddWithValue("@p1", 8);
                using (var reader = cmd.ExecuteReader())
                {
                    reader.Read();
                    Assert.That(reader[0], Is.EqualTo(8));
                    Assert.That(reader[1], Is.EqualTo(8));
                }
            }
        }

        [Test]
        public void GenericParameter()
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT @p1, @p2, @p3, @p4", conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter<int>("p1", 8));
                cmd.Parameters.Add(new NpgsqlParameter<short>("p2", 8) { NpgsqlDbType = NpgsqlDbType.Integer });
                cmd.Parameters.Add(new NpgsqlParameter<string>("p3", "hello"));
                cmd.Parameters.Add(new NpgsqlParameter<char[]>("p4", new[] { 'f', 'o', 'o' }));
                using (var reader = cmd.ExecuteReader())
                {
                    reader.Read();
                    Assert.That(reader.GetInt32(0), Is.EqualTo(8));
                    Assert.That(reader.GetInt32(1), Is.EqualTo(8));
                    Assert.That(reader.GetString(2), Is.EqualTo("hello"));
                    Assert.That(reader.GetString(3), Is.EqualTo("foo"));
                }
            }
        }

        [Test]
        public void CommandTextNotSet()
        {
            using (var conn = OpenConnection())
            {
                using (var cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    Assert.That(cmd.ExecuteNonQuery, Throws.Exception.TypeOf<InvalidOperationException>());
                    cmd.CommandText = null;
                    Assert.That(cmd.ExecuteNonQuery, Throws.Exception.TypeOf<InvalidOperationException>());
                    cmd.CommandText = "";
                }

                using (var cmd = conn.CreateCommand())
                    Assert.That(cmd.ExecuteNonQuery, Throws.Exception.TypeOf<InvalidOperationException>());
            }
        }

        [Test]
        public void ExecuteScalar()
        {
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TEMP TABLE data (name TEXT)");
                using (var command = new NpgsqlCommand("SELECT name FROM data", conn))
                {
                    Assert.That(command.ExecuteScalar(), Is.Null);

                    conn.ExecuteNonQuery(@"INSERT INTO data (name) VALUES (NULL)");
                    Assert.That(command.ExecuteScalar(), Is.EqualTo(DBNull.Value));

                    conn.ExecuteNonQuery(@"TRUNCATE data");
                    for (var i = 0; i < 2; i++)
                        conn.ExecuteNonQuery("INSERT INTO data (name) VALUES ('X')");
                    Assert.That(command.ExecuteScalar(), Is.EqualTo("X"));
                }
            }
        }

        [Test]
        public void ExecuteNonQuery()
        {
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TEMP TABLE data (name TEXT)");
                using (var cmd = new NpgsqlCommand { Connection = conn })
                {
                    cmd.CommandText = "INSERT INTO data (name) VALUES ('John')";
                    Assert.That(cmd.ExecuteNonQuery(), Is.EqualTo(1));

                    cmd.CommandText = "INSERT INTO data (name) VALUES ('John'); INSERT INTO data (name) VALUES ('John')";
                    Assert.That(cmd.ExecuteNonQuery(), Is.EqualTo(2));

                    cmd.CommandText = $"INSERT INTO data (name) VALUES ('{new string('x', conn.Settings.WriteBufferSize)}')";
                    Assert.That(cmd.ExecuteNonQuery(), Is.EqualTo(1));

                    cmd.Parameters.AddWithValue("not_used", DBNull.Value);
                    Assert.That(cmd.ExecuteNonQuery(), Is.EqualTo(1));
                }
            }
        }

        [Test, Description("Makes sure a command is unusable after it is disposed")]
        public void Dispose()
        {
            using (var conn = OpenConnection())
            {
                var cmd = new NpgsqlCommand("SELECT 1", conn);
                cmd.Dispose();
                Assert.That(() => cmd.ExecuteScalar(), Throws.Exception.TypeOf<ObjectDisposedException>());
                Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception.TypeOf<ObjectDisposedException>());
                Assert.That(() => cmd.ExecuteReader(), Throws.Exception.TypeOf<ObjectDisposedException>());
                Assert.That(() => cmd.Prepare(), Throws.Exception.TypeOf<ObjectDisposedException>());
            }
        }

        [Test, Description("Disposing a command with an open reader does not close the reader. This is the SqlClient behavior.")]
        public void DisposeCommandDoesNotCloseReader()
        {
            using (var conn = OpenConnection())
            {
                var cmd = new NpgsqlCommand("SELECT 1, 2", conn);
                cmd.ExecuteReader();
                cmd.Dispose();
                cmd = new NpgsqlCommand("SELECT 3", conn);
                Assert.That(() => cmd.ExecuteScalar(), Throws.Exception.TypeOf<NpgsqlOperationInProgressException>());
            }
        }

        [Test]
        public void StringEscapeSyntax()
        {
            using (var conn = OpenConnection())
            {

                //the next command will fail on earlier postgres versions, but that is not a bug in itself.
                try
                {
                    conn.ExecuteNonQuery("set standard_conforming_strings=off;set escape_string_warning=off");
                }
                catch
                {
                }
                var cmdTxt = "select :par";
                var command = new NpgsqlCommand(cmdTxt, conn);
                var arrCommand = new NpgsqlCommand(cmdTxt, conn);
                var testStrPar = "This string has a single quote: ', a double quote: \", and a backslash: \\";
                var testArrPar = new string[,] {{testStrPar, ""}, {testStrPar, testStrPar}};
                command.Parameters.AddWithValue(":par", testStrPar);
                using (var rdr = command.ExecuteReader())
                {
                    rdr.Read();
                    Assert.AreEqual(rdr.GetString(0), testStrPar);
                }
                arrCommand.Parameters.AddWithValue(":par", testArrPar);
                using (var rdr = arrCommand.ExecuteReader())
                {
                    rdr.Read();
                    Assert.AreEqual(((string[,]) rdr.GetValue(0))[0, 0], testStrPar);
                }

                try //the next command will fail on earlier postgres versions, but that is not a bug in itself.
                {
                    conn.ExecuteNonQuery("set standard_conforming_strings=on;set escape_string_warning=on");
                }
                catch
                {
                }
                using (var rdr = command.ExecuteReader())
                {
                    rdr.Read();
                    Assert.AreEqual(rdr.GetString(0), testStrPar);
                }
                using (var rdr = arrCommand.ExecuteReader())
                {
                    rdr.Read();
                    Assert.AreEqual(((string[,]) rdr.GetValue(0))[0, 0], testStrPar);
                }
            }
        }

        [Test]
        public void ParameterAndOperatorUnclear()
        {
            using (var conn = OpenConnection())
            {
                //Without parenthesis the meaning of [, . and potentially other characters is
                //a syntax error. See comment in NpgsqlCommand.GetClearCommandText() on "usually-redundant parenthesis".
                using (var command = new NpgsqlCommand("select :arr[2]", conn))
                {
                    command.Parameters.AddWithValue(":arr", new int[] {5, 4, 3, 2, 1});
                    using (var rdr = command.ExecuteReader())
                    {
                        rdr.Read();
                        Assert.AreEqual(rdr.GetInt32(0), 4);
                    }
                }
            }
        }

        [Test]
        public void StatementMappedOutputParameters()
        {
            using (var conn = OpenConnection())
            {
                var command = new NpgsqlCommand("select 3, 4 as param1, 5 as param2, 6;", conn);

                var p = new NpgsqlParameter("param2", NpgsqlDbType.Integer);
                p.Direction = ParameterDirection.Output;
                p.Value = -1;
                command.Parameters.Add(p);

                p = new NpgsqlParameter("param1", NpgsqlDbType.Integer);
                p.Direction = ParameterDirection.Output;
                p.Value = -1;
                command.Parameters.Add(p);

                p = new NpgsqlParameter("p", NpgsqlDbType.Integer);
                p.Direction = ParameterDirection.Output;
                p.Value = -1;
                command.Parameters.Add(p);

                command.ExecuteNonQuery();

                Assert.AreEqual(4, command.Parameters["param1"].Value);
                Assert.AreEqual(5, command.Parameters["param2"].Value);
                //Assert.AreEqual(-1, command.Parameters["p"].Value); //Which is better, not filling this or filling this with an unmapped value?
            }
        }

        [Test]
        public void CaseInsensitiveParameterNames()
        {
            using (var conn = OpenConnection())
            using (var command = new NpgsqlCommand("select :p1", conn))
            {
                command.Parameters.Add(new NpgsqlParameter("P1", NpgsqlDbType.Integer)).Value = 5;
                var result = command.ExecuteScalar();
                Assert.AreEqual(5, result);
            }
        }

        [Test]
        public void TestBug1006158OutputParameters()
        {
            using (var conn = OpenConnection())
            {
                const string createFunction =
                    @"CREATE OR REPLACE FUNCTION pg_temp.more_params(OUT a integer, OUT b boolean) AS
            $BODY$DECLARE
                BEGIN
                    a := 3;
                    b := true;
                END;$BODY$
              LANGUAGE 'plpgsql' VOLATILE;";

                var command = new NpgsqlCommand(createFunction, conn);
                command.ExecuteNonQuery();

                command = new NpgsqlCommand("pg_temp.more_params", conn);
                command.CommandType = CommandType.StoredProcedure;

                command.Parameters.Add(new NpgsqlParameter("a", DbType.Int32));
                command.Parameters[0].Direction = ParameterDirection.Output;
                command.Parameters.Add(new NpgsqlParameter("b", DbType.Boolean));
                command.Parameters[1].Direction = ParameterDirection.Output;

                var result = command.ExecuteScalar();

                Assert.AreEqual(3, command.Parameters[0].Value);
                Assert.AreEqual(true, command.Parameters[1].Value);
            }
        }

        [Test]
        public void TestErrorInPreparedStatementCausesReleaseConnectionToThrowException()
        {
            using (var conn = OpenConnection())
            {
                // This is caused by having an error with the prepared statement and later, Npgsql is trying to release the plan as it was successful created.
                var cmd = new NpgsqlCommand("sele", conn);
                Assert.That(() => cmd.Prepare(), Throws.Exception.TypeOf<PostgresException>());
            }
        }

        [Test]
        public void Bug1010788UpdateRowSource()
        {
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TEMP TABLE data (id SERIAL PRIMARY KEY, name TEXT)");
                var command = new NpgsqlCommand("SELECT * FROM data", conn);
                Assert.AreEqual(UpdateRowSource.Both, command.UpdatedRowSource);

                var cmdBuilder = new NpgsqlCommandBuilder();
                var da = new NpgsqlDataAdapter(command);
                cmdBuilder.DataAdapter = da;
                Assert.IsNotNull(da.SelectCommand);
                Assert.IsNotNull(cmdBuilder.DataAdapter);

                var updateCommand = cmdBuilder.GetUpdateCommand();
                Assert.AreEqual(UpdateRowSource.None, updateCommand.UpdatedRowSource);
            }
        }

        [Test]
        public void TableDirect()
        {
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TEMP TABLE data (name TEXT)");
                conn.ExecuteNonQuery(@"INSERT INTO data (name) VALUES ('foo')");
                using (var cmd = new NpgsqlCommand("data", conn) { CommandType = CommandType.TableDirect })
                using (var rdr = cmd.ExecuteReader())
                {
                    Assert.That(rdr.Read(), Is.True);
                    Assert.That(rdr["name"], Is.EqualTo("foo"));
                }
            }
        }

        [Test]
        [TestCase(CommandBehavior.Default)]
        [TestCase(CommandBehavior.SequentialAccess)]
        public void InputAndOutputParameters(CommandBehavior behavior)
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT @c-1 AS c, @a+2 AS b", conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter("a", 3));
                var b = new NpgsqlParameter { ParameterName = "b", Direction = ParameterDirection.Output };
                cmd.Parameters.Add(b);
                var c = new NpgsqlParameter { ParameterName = "c", Direction = ParameterDirection.InputOutput, Value = 4 };
                cmd.Parameters.Add(c);
                using (cmd.ExecuteReader(behavior))
                {
                    Assert.AreEqual(5, b.Value);
                    Assert.AreEqual(3, c.Value);
                }
            }
        }

        [Test]
        public void SendUnknown([Values(PrepareOrNot.NotPrepared, PrepareOrNot.Prepared)] PrepareOrNot prepare)
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT @p::TIMESTAMP", conn))
            {
                cmd.CommandText = "SELECT @p::TIMESTAMP";
                cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Unknown) { Value = "2008-1-1" });
                if (prepare == PrepareOrNot.Prepared)
                    cmd.Prepare();
                using (var reader = cmd.ExecuteReader())
                {
                    reader.Read();
                    Assert.That(reader.GetValue(0), Is.EqualTo(new DateTime(2008, 1, 1)));
                }
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/503")]
        public void InvalidUTF8()
        {
            const string badString = "SELECT 'abc\uD801\uD802d'";
            using (var conn = OpenConnection())
            {
                Assert.That(() => conn.ExecuteScalar(badString), Throws.Exception.TypeOf<EncoderFallbackException>());
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/395")]
        public void UseAcrossConnectionChange([Values(PrepareOrNot.Prepared, PrepareOrNot.NotPrepared)] PrepareOrNot prepare)
        {
            using (var conn1 = OpenConnection())
            using (var conn2 = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT 1", conn1))
            {
                if (prepare == PrepareOrNot.Prepared)
                    cmd.Prepare();
                cmd.Connection = conn2;
                Assert.That(cmd.IsPrepared, Is.False);
                if (prepare == PrepareOrNot.Prepared)
                    cmd.Prepare();
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1));
            }
        }

        [Test, Description("CreateCommand before connection open")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/565")]
        public void CreateCommandBeforeConnectionOpen()
        {
            using (var conn = new NpgsqlConnection(ConnectionString)) {
                var cmd = new NpgsqlCommand("SELECT 1", conn);
                conn.Open();
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo(1));
            }
        }

        [Test]
        public void BadConnection()
        {
            var cmd = new NpgsqlCommand("SELECT 1");
            Assert.That(() => cmd.ExecuteScalar(), Throws.Exception.TypeOf<InvalidOperationException>());

            using (var conn = new NpgsqlConnection(ConnectionString))
            {
                cmd = new NpgsqlCommand("SELECT 1", conn);
                Assert.That(() => cmd.ExecuteScalar(), Throws.Exception.TypeOf<InvalidOperationException>());
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/831")]
        [Timeout(10000)]
        public void ManyParameters()
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT 1", conn))
            {
                for (var i = 0; i < conn.Settings.WriteBufferSize; i++)
                    cmd.Parameters.Add(new NpgsqlParameter("p" + i, 8));
                cmd.ExecuteNonQuery();
            }
        }

        [Test, Description("Bypasses PostgreSQL's int16 limitation on the number of parameters")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/831")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/858")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/2703")]
        public void TooManyParameters()
        {
            using var conn = OpenConnection();
            using var cmd = new NpgsqlCommand { Connection = conn };
            var sb = new StringBuilder("SOME RANDOM SQL ");
            for (var i = 0; i < short.MaxValue + 1; i++)
            {
                var paramName = "p" + i;
                cmd.Parameters.Add(new NpgsqlParameter(paramName, 8));
                if (i > 0)
                    sb.Append(", ");
                sb.Append('@');
                sb.Append(paramName);
            }
            cmd.CommandText = sb.ToString();

            Assert.That(() => cmd.ExecuteNonQuery(), Throws.Exception
                .InstanceOf<NpgsqlException>()
                .With.Message.EqualTo("A statement cannot have more than 32767 parameters"));
            Assert.That(() => cmd.Prepare(), Throws.Exception
                .InstanceOf<NpgsqlException>()
                .With.Message.EqualTo("A statement cannot have more than 32767 parameters"));
        }

        [Test, Description("An individual statement cannot have more than 32767 parameters, but a command can (across multiple statements).")]
        [IssueLink("https://github.com/npgsql/npgsql/issues/1199")]
        public void ManyParametersAcrossStatements()
        {
            // Create a command with 1000 statements which have 70 params each
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand { Connection = conn })
            {
                var paramIndex = 0;
                var sb = new StringBuilder();
                for (var statementIndex = 0; statementIndex < 1000; statementIndex++)
                {
                    if (statementIndex > 0)
                        sb.Append("; ");
                    sb.Append("SELECT ");
                    var startIndex = paramIndex;
                    var endIndex = paramIndex + 70;
                    for (; paramIndex < endIndex; paramIndex++)
                    {
                        var paramName = "p" + paramIndex;
                        cmd.Parameters.Add(new NpgsqlParameter(paramName, 8));
                        if (paramIndex > startIndex)
                            sb.Append(", ");
                        sb.Append('@');
                        sb.Append(paramName);
                    }
                }

                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }

        }

        [Test, Description("Makes sure that Npgsql doesn't attempt to send all data before the user can start reading. That would cause a deadlock.")]
        public void ReadWriteDeadlock()
        {
            // We're going to send a large multistatement query that would exhaust both the client's and server's
            // send and receive buffers (assume 64k per buffer).
            var data = new string('x', 1024);
            using (var conn = OpenConnection())
            {
                var sb = new StringBuilder();
                for (var i = 0; i < 500; i++)
                    sb.Append("SELECT @p;");
                using (var cmd = new NpgsqlCommand(sb.ToString(), conn))
                {
                    cmd.Parameters.AddWithValue("p", NpgsqlDbType.Text, data);
                    using (var reader = cmd.ExecuteReader())
                    {
                        for (var i = 0; i < 500; i++)
                        {
                            reader.Read();
                            Assert.That(reader.GetString(0), Is.EqualTo(data));
                            reader.NextResult();
                        }
                    }
                }
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/1037")]
        public void Statements()
        {
            // See also ReaderTests.Statements()
            using (var conn = OpenConnection())
            {
                conn.ExecuteNonQuery("CREATE TEMP TABLE data (name TEXT) WITH OIDS");
                using (var cmd = new NpgsqlCommand(
                    "INSERT INTO data (name) VALUES (@p1);" +
                    "UPDATE data SET name='b' WHERE name=@p2",
                    conn)
                )
                {
                    cmd.Parameters.AddWithValue("p1", "foo");
                    cmd.Parameters.AddWithValue("p2", "bar");
                    cmd.ExecuteNonQuery();

                    Assert.That(cmd.Statements, Has.Count.EqualTo(2));
                    Assert.That(cmd.Statements[0].SQL, Is.EqualTo("INSERT INTO data (name) VALUES ($1)"));
                    Assert.That(cmd.Statements[0].InputParameters[0].ParameterName, Is.EqualTo("p1"));
                    Assert.That(cmd.Statements[0].InputParameters[0].Value, Is.EqualTo("foo"));
                    Assert.That(cmd.Statements[0].StatementType, Is.EqualTo(StatementType.Insert));
                    Assert.That(cmd.Statements[0].Rows, Is.EqualTo(1));
                    Assert.That(cmd.Statements[0].OID, Is.Not.EqualTo(0));
                    Assert.That(cmd.Statements[1].SQL, Is.EqualTo("UPDATE data SET name='b' WHERE name=$1"));
                    Assert.That(cmd.Statements[1].InputParameters[0].ParameterName, Is.EqualTo("p2"));
                    Assert.That(cmd.Statements[1].InputParameters[0].Value, Is.EqualTo("bar"));
                    Assert.That(cmd.Statements[1].StatementType, Is.EqualTo(StatementType.Update));
                    Assert.That(cmd.Statements[1].Rows, Is.EqualTo(0));
                    Assert.That(cmd.Statements[1].OID, Is.EqualTo(0));
                }
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/1429")]
        public void SameCommandDifferentParamValues()
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT @p", conn))
            {
                cmd.Parameters.AddWithValue("p", 8);
                cmd.ExecuteNonQuery();

                cmd.Parameters[0].Value = 9;
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo(9));
            }
        }

        [Test, IssueLink("https://github.com/npgsql/npgsql/issues/1429")]
        public void SameCommandDifferentParamInstances()
        {
            using (var conn = OpenConnection())
            using (var cmd = new NpgsqlCommand("SELECT @p", conn))
            {
                cmd.Parameters.AddWithValue("p", 8);
                cmd.ExecuteNonQuery();

                cmd.Parameters.RemoveAt(0);
                cmd.Parameters.AddWithValue("p", 9);
                Assert.That(cmd.ExecuteScalar(), Is.EqualTo(9));
            }
        }
    }
}
