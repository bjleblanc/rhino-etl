using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using Boo.Lang;

namespace Rhino.ETL
{
	using Engine;
	using Retlang;

	public class DataDestination : BaseDataElement<DataDestination>
	{
		private bool hasCompleted = false;
		private bool firstCall = true;
		private ICallable initializeBlock, onRowBlock, cleanUpBlock;
		private int batchSize = 250;

		public int BatchSize
		{
			get { return batchSize; }
			set { batchSize = value; }
		}

		[Browsable(false)]
		public ICallable InitializeBlock
		{
			get { return initializeBlock; }
			set { initializeBlock = value; }
		}

		[Browsable(false)]
		public ICallable OnRowBlock
		{
			get { return onRowBlock; }
			set { onRowBlock = value; }
		}

		[Browsable(false)]
		public ICallable CleanUpBlock
		{
			get { return cleanUpBlock; }
			set { cleanUpBlock = value; }
		}

		protected override bool CustomActionSpecified
		{
			get { return onRowBlock != null; }
		}

		public DataDestination(string name)
			: base(name)
		{
			EtlConfigurationContext.Current.AddDestination(name, this);
		}

		private void EnsureInitializeWasCalled()
		{
			if (firstCall && initializeBlock != null)
			{
				initializeBlock.Call(new object[] { Items });
			}
			firstCall = false;
		}

		public void Complete()
		{
			EnsureInitializeWasCalled();
			hasCompleted = true;
			if (onRowBlock != null && cleanUpBlock != null)
			{
				cleanUpBlock.Call(new object[] { Items });
			}

		}


		public override void Start(IProcessContext context, string inputName)
		{
			On<IList<IMessageEnvelope<Row>>> callback = delegate(IList<IMessageEnvelope<Row>> rowMsgs)
			{
				if (hasCompleted)
					throw new InvalidOperationException("Cannot process rows to a destination after it has been marked complete");
				EnsureInitializeWasCalled();
				List<Row> rows = new List<Row>();
				foreach (IMessageEnvelope<Row> envelope in rowMsgs)
				{
					rows.Add(envelope.Message);
				}
				ProcessOutput(rows);
			};
			BatchSubscriber<Row> batchingSubscriber = new BatchSubscriber<Row>(callback, context, BatchSize);

			context.Subscribe<object>(new TopicEquals(inputName + Messages.Done), delegate
			{
				batchingSubscriber.Flush();
				Complete();
				context.Stop();
			});
			context.Subscribe<Row>(new TopicEquals(inputName), batchingSubscriber.ReceiveMessage);
		}

		public void ProcessOutput(IList<Row> rows)
		{
			if (CustomActionSpecified == false)
			{
				SendToDatabase(rows);
			}
			else
			{
				foreach (Row copyRow in rows)
				{
					onRowBlock.Call(new object[] { Items, copyRow });
				}
			}
		}

		private void SendToDatabase(IEnumerable<Row> copyRows)
		{
			foreach (Row row in copyRows)
			{
				using (IDbConnection connection = ConnectionFactory.AcquireConnection())
				using (IDbCommand command = connection.CreateCommand())
				{
					command.CommandText = Command;
					AddParameters(command);
					foreach (string name in row.Columns)
					{
						IDbDataParameter parameter = command.CreateParameter();
						parameter.ParameterName = name;
						object value = row[name] ?? DBNull.Value;
						parameter.Value = value;
						command.Parameters.Add(parameter);
					}
					command.ExecuteNonQuery();
				}
			}
		}
	}
}