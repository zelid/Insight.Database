﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using Glimpse.Ado.AlternateType;
using Insight.Database;
using Insight.Database.Providers.Glimpse;
using NUnit.Framework;

namespace Insight.Tests
{
	/// <summary>
	/// Tests that the mini-profiler connection wrapper does not interfere with SQL parameter detection.
	/// </summary>
	[TestFixture]
	public class GlimpseTests : BaseDbTest
	{
		public override void SetUpFixture()
		{
			GlimpseInsightDbProvider.RegisterProvider();

			base.SetUpFixture();
		}

		/// <summary>
		/// Make sure that we can connect to the database
		/// </summary>
		[Test]
		public void TestProfiledSqlQuery()
		{
			var profiled = new GlimpseDbConnection(_connection);
			var result = profiled.QuerySql<int>("SELECT @p --GLIMPSE", new { p = 1 }).First();

			Assert.AreEqual((int)1, result);
		}

		/// <summary>
		/// Make sure that a profiled connection still can auto-detect the parameters.
		/// </summary>
		[Test]
		public void TestProfiledStoredProcWithParameters()
		{
			using (var connection = _connectionStringBuilder.OpenWithTransaction())
			{
				connection.ExecuteSql("CREATE PROC InsightTestProcGlimpse (@Value int = 5) AS SELECT Value=@Value");

				var profiled = new GlimpseDbConnection(connection);
				var result = profiled.Query<int>("InsightTestProcGlimpse", new { Value = 1 }).First();

				Assert.AreEqual((int)1, result);
			}
		}
	}
}
