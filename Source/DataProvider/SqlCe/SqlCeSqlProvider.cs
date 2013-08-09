﻿using System;
using System.Text;

namespace LinqToDB.DataProvider.SqlCe
{
	using Extensions;
	using SqlBuilder;
	using SqlProvider;

	public class SqlCeSqlProvider : BasicSqlProvider
	{
		public SqlCeSqlProvider(SqlProviderFlags sqlProviderFlags) : base(sqlProviderFlags)
		{
			SqlProviderFlags.IsCountSubQuerySupported  = false;
			SqlProviderFlags.IsApplyJoinSupported      = true;
			SqlProviderFlags.IsInsertOrUpdateSupported = false;
		}

		protected override string FirstFormat  { get { return SelectQuery.Select.SkipValue == null ? "TOP ({0})" :                null; } }
		protected override string LimitFormat  { get { return SelectQuery.Select.SkipValue != null ? "FETCH NEXT {0} ROWS ONLY" : null; } }
		protected override string OffsetFormat { get { return "OFFSET {0} ROWS"; } }
		protected override bool   OffsetFirst  { get { return true;              } }

		public override int CommandCount(SelectQuery selectQuery)
		{
			return selectQuery.IsInsert && selectQuery.Insert.WithIdentity ? 2 : 1;
		}

		protected override void BuildCommand(int commandNumber, StringBuilder sb)
		{
			sb.AppendLine("SELECT @@IDENTITY");
		}

		protected override ISqlProvider CreateSqlProvider()
		{
			return new SqlCeSqlProvider(SqlProviderFlags);
		}

		public override ISqlExpression ConvertExpression(ISqlExpression expr)
		{
			expr = base.ConvertExpression(expr);

			if (expr is SqlBinaryExpression)
			{
				var be = (SqlBinaryExpression)expr;

				switch (be.Operation)
				{
					case "%":
						return be.Expr1.SystemType.IsIntegerType()?
							be :
							new SqlBinaryExpression(
								typeof(int),
								new SqlFunction(typeof(int), "Convert", SqlDataType.Int32, be.Expr1),
								be.Operation,
								be.Expr2,
								be.Precedence);
				}
			}
			else if (expr is SqlFunction)
			{
				var func = (SqlFunction)expr;

				switch (func.Name)
				{
					case "Convert" :
						switch (Type.GetTypeCode(func.SystemType.ToUnderlying()))
						{
							case TypeCode.UInt64 :
								if (func.Parameters[1].SystemType.IsFloatType())
									return new SqlFunction(
										func.SystemType,
										func.Name,
										func.Precedence,
										func.Parameters[0],
										new SqlFunction(func.SystemType, "Floor", func.Parameters[1]));

								break;

							case TypeCode.DateTime :
								var type1 = func.Parameters[1].SystemType.ToUnderlying();

								if (IsTimeDataType(func.Parameters[0]))
								{
									if (type1 == typeof(DateTime) || type1 == typeof(DateTimeOffset))
										return new SqlExpression(
											func.SystemType, "Cast(Convert(NChar, {0}, 114) as DateTime)", Precedence.Primary, func.Parameters[1]);

									if (func.Parameters[1].SystemType == typeof(string))
										return func.Parameters[1];

									return new SqlExpression(
										func.SystemType, "Convert(NChar, {0}, 114)", Precedence.Primary, func.Parameters[1]);
								}

								if (type1 == typeof(DateTime) || type1 == typeof(DateTimeOffset))
								{
									if (IsDateDataType(func.Parameters[0], "Datetime"))
										return new SqlExpression(
											func.SystemType, "Cast(Floor(Cast({0} as Float)) as DateTime)", Precedence.Primary, func.Parameters[1]);
								}

								break;
						}

						break;
				}
			}

			return expr;
		}

		protected override void BuildFunction(StringBuilder sb, SqlFunction func)
		{
			func = ConvertFunctionParameters(func);
			base.BuildFunction(sb, func);
		}

		public override SelectQuery Finalize(SelectQuery selectQuery)
		{
			selectQuery = base.Finalize(selectQuery);

			new QueryVisitor().Visit(selectQuery.Select, element =>
			{
				if (element.ElementType == QueryElementType.SqlParameter)
				{
					((SqlParameter)element).IsQueryParameter = false;
					selectQuery.IsParameterDependent = true;
				}
			});

			switch (selectQuery.QueryType)
			{
				case QueryType.Delete :
					selectQuery = GetAlternativeDelete(selectQuery);
					selectQuery.From.Tables[0].Alias = "$";
					break;

				case QueryType.Update :
					selectQuery = GetAlternativeUpdate(selectQuery);
					break;
			}

			return selectQuery;
		}

		protected override void BuildDataType(StringBuilder sb, SqlDataType type, bool createDbType = false)
		{
			switch (type.DataType)
			{
				case DataType.Char          : base.BuildDataType(sb, new SqlDataType(DataType.NChar,    type.Length)); break;
				case DataType.VarChar       : base.BuildDataType(sb, new SqlDataType(DataType.NVarChar, type.Length)); break;
				case DataType.SmallMoney    : sb.Append("Decimal(10,4)");   break;
#if !MONO
				case DataType.DateTime2     :
#endif
				case DataType.Time          :
				case DataType.Date          :
				case DataType.SmallDateTime : sb.Append("DateTime");        break;
				default                     : base.BuildDataType(sb, type); break;
			}
		}

		protected override void BuildFromClause(StringBuilder sb)
		{
			if (!SelectQuery.IsUpdate)
				base.BuildFromClause(sb);
		}

		protected override void BuildOrderByClause(StringBuilder sb)
		{
			if (SelectQuery.OrderBy.Items.Count == 0 && SelectQuery.Select.SkipValue != null)
			{
				AppendIndent(sb);

				sb.Append("ORDER BY").AppendLine();

				Indent++;

				AppendIndent(sb);

				BuildExpression(sb, SelectQuery.Select.Columns[0].Expression);
				sb.AppendLine();

				Indent--;
			}
			else
				base.BuildOrderByClause(sb);
		}

		protected override void BuildColumnExpression(StringBuilder sb, ISqlExpression expr, string alias, ref bool addAlias)
		{
			var wrap = false;

			if (expr.SystemType == typeof(bool))
			{
				if (expr is SelectQuery.SearchCondition)
					wrap = true;
				else
				{
					var ex = expr as SqlExpression;
					wrap = ex != null && ex.Expr == "{0}" && ex.Parameters.Length == 1 && ex.Parameters[0] is SelectQuery.SearchCondition;
				}
			}

			if (wrap) sb.Append("CASE WHEN ");
			base.BuildColumnExpression(sb, expr, alias, ref addAlias);
			if (wrap) sb.Append(" THEN 1 ELSE 0 END");
		}

		public override object Convert(object value, ConvertType convertType)
		{
			switch (convertType)
			{
				case ConvertType.NameToQueryParameter:
				case ConvertType.NameToCommandParameter:
				case ConvertType.NameToSprocParameter:
					return "@" + value;

				case ConvertType.NameToQueryField:
				case ConvertType.NameToQueryFieldAlias:
				case ConvertType.NameToQueryTableAlias:
					{
						var name = value.ToString();

						if (name.Length > 0 && name[0] == '[')
							return value;
					}

					return "[" + value + "]";

				case ConvertType.NameToDatabase:
				case ConvertType.NameToOwner:
				case ConvertType.NameToQueryTable:
					if (value != null)
					{
						var name = value.ToString();

						if (name.Length > 0 && name[0] == '[')
							return value;

						if (name.IndexOf('.') > 0)
							value = string.Join("].[", name.Split('.'));

						return "[" + value + "]";
					}

					break;

				case ConvertType.SprocParameterToName:
					if (value != null)
					{
						var str = value.ToString();
						return str.Length > 0 && str[0] == '@'? str.Substring(1): str;
					}
					break;
			}

			return value;
		}

		protected override void BuildCreateTableIdentityAttribute2(StringBuilder sb, SqlField field)
		{
			sb.Append("IDENTITY");
		}
	}
}