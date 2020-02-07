﻿#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LinqToDB.Linq.Builder
{
	using Common;
	using Extensions;
	using LinqToDB.Expressions;
	using Mapping;
	using SqlQuery;

	partial class TableBuilder
	{
		public class AssociatedTableContext : TableContext
		{
			public readonly TableContext             ParentAssociation;
			public readonly SqlJoinedTable           ParentAssociationJoin;
			public readonly AssociationDescriptor    Association;
			public readonly bool                     IsList;
			public readonly bool                     IsSubQuery;
			public          int                      RegularConditionCount;
			public          LambdaExpression         ExpressionPredicate;

			public override IBuildContext Parent
			{
				get { return ParentAssociation.Parent; }
				set { }
			}

			static MethodInfo _selectManyMethodInfo = MemberHelper.MethodOf((IQueryable<int> q) =>
				q.SelectMany(i => new int [0], (i, c) => i)).GetGenericMethodDefinition();

			Dictionary<ISqlExpression, SqlField> _replaceMap;
			internal IBuildContext               _innerContext;

			public AssociatedTableContext(
				[JetBrains.Annotations.NotNull] ExpressionBuilder     builder,
				[JetBrains.Annotations.NotNull] TableContext          parent,
				[JetBrains.Annotations.NotNull] AssociationDescriptor association,
				                                bool                  forceLeft,
				                                bool                  asSubquery
			)
				: base(builder, asSubquery ? new SelectQuery() { ParentSelect = parent.SelectQuery } : parent.SelectQuery)
			{
				if (builder     == null) throw new ArgumentNullException(nameof(builder));
				if (parent      == null) throw new ArgumentNullException(nameof(parent));
				if (association == null) throw new ArgumentNullException(nameof(association));

				var type = association.MemberInfo.GetMemberType();
				var left = forceLeft || association.CanBeNull;

				if (typeof(IEnumerable).IsSameOrParentOf(type))
				{
					var eTypes = type.GetGenericArguments(typeof(IEnumerable<>));
					type       = eTypes != null && eTypes.Length > 0 ? eTypes[0] : type.GetListItemType();
					IsList     = true;
				}

				OriginalType       = type;
				ObjectType         = GetObjectType();
				EntityDescriptor   = Builder.MappingSchema.GetEntityDescriptor(ObjectType);
				InheritanceMapping = EntityDescriptor.InheritanceMapping;
				SqlTable           = new SqlTable(builder.MappingSchema, ObjectType);

				Association       = association;
				ParentAssociation = parent;
				IsSubQuery        = asSubquery;

				if (asSubquery)
				{
					BuildSubQuery(builder, parent, association, left);
					Init(false);
					return;
				}

				SqlJoinedTable join;

				var queryMethod = Association.GetQueryMethod(parent.ObjectType, ObjectType);
				if (queryMethod != null)
				{
					var selectManyMethod = GetAssociationQueryExpression(Expression.Constant(builder.DataContext),
						queryMethod.Parameters[0], parent.ObjectType, parent.Expression, queryMethod);

					var ownerTableSource = SelectQuery.From.Tables[0];

					var buildInfo = new BuildInfo(this, selectManyMethod, new SelectQuery()) { IsAssociationBuilt = true };
					_innerContext = builder.BuildSequence(buildInfo);

					if (Association.CanBeNull)
					{
						_innerContext = new DefaultIfEmptyBuilder.DefaultIfEmptyContext(buildInfo.Parent, _innerContext, null);
					}

					var associationQuery = _innerContext.SelectQuery;

					if (associationQuery.Select.From.Tables.Count < 1)
						throw new LinqToDBException("Invalid association query. It is not possible to inline query.");

					var foundIndex = associationQuery.Select.From.Tables.FindIndex(t =>
						t.Source is SqlTable sqlTable && QueryHelper.IsEqualTables(sqlTable, parent.SqlTable));

					// try to search table by object type
					// TODO: review maybe there are another ways to do that
					if (foundIndex < 0)
						foundIndex = associationQuery.Select.From.Tables.FindIndex(t =>
							t.Source is SqlTable sqlTable && sqlTable.ObjectType == parent.SqlTable.ObjectType);

					if (foundIndex < 0)
						throw new LinqToDBException("Invalid association query. It is not possible to inline query. Can not find owner table.");

					var sourceToReplace = associationQuery.Select.From.Tables[foundIndex];

					if (left)
					{
						foreach (var joinedTable in sourceToReplace.Joins)
						{
							if (joinedTable.JoinType == JoinType.Inner)
								joinedTable.JoinType = JoinType.Left;
							else if (joinedTable.JoinType == JoinType.CrossApply)
								joinedTable.JoinType = JoinType.OuterApply;

							joinedTable.IsWeak = true;
						}
					}

					ownerTableSource.Joins.AddRange(sourceToReplace.Joins);

					// prepare fields mapping to replace fields that will be generated by association query
					_replaceMap =
						((SqlTable)sourceToReplace.Source).Fields.Values.ToDictionary(f => (ISqlExpression)f,
							f => parent.SqlTable.Fields[f.Name]);

					ownerTableSource.Walk(new WalkOptions(), e =>
					{
						if (_replaceMap.TryGetValue(e, out var newField))
							return newField;
						return e;
					});

					ParentAssociationJoin = sourceToReplace.Joins.FirstOrDefault();
					join = ParentAssociationJoin;

					// add rest of tables
					SelectQuery.From.Tables.AddRange(associationQuery.Select.From.Tables.Where(t => t != sourceToReplace));

					//TODO: Change AssociatedTableContext base class
					//SqlTable = null;
				}
				else
				{
					var psrc = parent.SelectQuery.From[parent.SqlTable];
					join = left ? SqlTable.WeakLeftJoin().JoinedTable : SqlTable.WeakInnerJoin().JoinedTable;

					ParentAssociationJoin = join;

					psrc.Joins.Add(join);
					BuildAssociationCondition(builder, parent, association, join.Condition);
				}

				SetTableAlias(association, join?.Table);

				Init(false);
			}

			private static void SetTableAlias(AssociationDescriptor association, SqlTableSource table)
			{
				if (!association.AliasName.IsNullOrEmpty() && table != null)
				{
					table.Alias = association.AliasName;
				}
				else
				{
					if (!Common.Configuration.Sql.AssociationAlias.IsNullOrEmpty() && table != null)
						table.Alias = string.Format(Common.Configuration.Sql.AssociationAlias,
							association.MemberInfo.Name);
				}
			}

			private void BuildAssociationCondition(ExpressionBuilder builder, TableContext parent, AssociationDescriptor association, SqlSearchCondition condition)
			{
				for (var i = 0; i < association.ThisKey.Length; i++)
				{
					if (!parent.SqlTable.Fields.TryGetValue(association.ThisKey[i], out var field1))
						throw new LinqException("Association key '{0}' not found for type '{1}.", association.ThisKey[i], parent.ObjectType);

					if (!SqlTable.Fields.TryGetValue(association.OtherKey[i], out var field2))
						throw new LinqException("Association key '{0}' not found for type '{1}.", association.OtherKey[i], ObjectType);

					ISqlPredicate predicate = new SqlPredicate.ExprExpr(
						field1, SqlPredicate.Operator.Equal, field2);

					predicate = builder.Convert(parent, predicate);

					condition.Conditions.Add(new SqlCondition(false, predicate));
				}

				if (ObjectType != OriginalType)
				{
					var predicate = Builder.MakeIsPredicate(this, OriginalType);

					if (predicate.GetType() != typeof(SqlPredicate.Expr))
						condition.Conditions.Add(new SqlCondition(false, predicate));
				}

				RegularConditionCount = condition.Conditions.Count;
				ExpressionPredicate = Association.GetPredicate(parent.ObjectType, ObjectType);

				if (ExpressionPredicate != null)
				{
					ExpressionPredicate = (LambdaExpression)Builder.ConvertExpressionTree(ExpressionPredicate);

					var expr = Builder.ConvertExpression(ExpressionPredicate.Body.Unwrap());

					Builder.BuildSearchCondition(
						new ExpressionContext(parent.Parent, new IBuildContext[] { parent, this }, ExpressionPredicate),
						expr,
						condition.Conditions,
						false);
				}
			}

			public LambdaExpression GetAssociationQueryExpressionAsSubQuery(Expression dataContextExpr, LambdaExpression queryMethod)
			{
				var resultParam = Expression.Parameter(ObjectType);

				var parentParameter = queryMethod.Parameters[0];

				var body = queryMethod.GetBody(parentParameter, dataContextExpr);
				body = Expression.Convert(body, typeof(IEnumerable<>).MakeGenericType(ObjectType));

				return Expression.Lambda(body, parentParameter);
			}

			private void BuildSubQuery(ExpressionBuilder builder, TableContext parent, AssociationDescriptor association, bool left)
			{
				var queryMethod = Association.GetQueryMethod(parent.ObjectType, ObjectType);
				if (queryMethod != null)
				{
					var subquery = GetAssociationQueryExpressionAsSubQuery(Expression.Constant(builder.DataContext), queryMethod);

					var ownerTableSource = parent.SelectQuery.From.Tables[0];

					// TODO: need to build without join here
					_innerContext = builder.BuildSequence(
						new BuildInfo(
							new ExpressionContext(null, new[] { parent }, subquery),
							subquery.Body,
							new SelectQuery() { ParentSelect = SelectQuery.ParentSelect }
							)
					{ IsAssociationBuilt = false });

					var associationQuery = _innerContext.SelectQuery;

					if (associationQuery.Select.From.Tables.Count < 1)
						throw new LinqToDBException("Invalid association query. It is not possible to inline query.");

					SelectQuery = associationQuery;
				}
				else
				{
					SelectQuery.From.Table(SqlTable);

					BuildAssociationCondition(builder, parent, association, SelectQuery.Where.SearchCondition);
				}

				SetTableAlias(association, SelectQuery.From.Tables[0]);
			}

			public Expression GetAssociationQueryExpression(Expression dataContextExpr, Expression parentObjExpression, Type parentType, Expression parentTableExpression,
				LambdaExpression queryMethod)
			{
				var resultParam = Expression.Parameter(ObjectType);

				var body    = queryMethod.GetBody(parentObjExpression ?? queryMethod.Parameters[0], dataContextExpr);
				body        = Expression.Convert(body, typeof(IEnumerable<>).MakeGenericType(ObjectType));
				queryMethod = Expression.Lambda(body, queryMethod.Parameters[0]);

				var selectManyMethodInfo = _selectManyMethodInfo.MakeGenericMethod(parentType, ObjectType, ObjectType);
				var resultLamba          = Expression.Lambda(resultParam, Expression.Parameter(parentType), resultParam);
				var selectManyMethod     = Expression.Call(null, selectManyMethodInfo, parentTableExpression, queryMethod, resultLamba);

				return selectManyMethod;
			}

			public override void BuildQuery<T>(Query<T> query, ParameterExpression queryParameter)
			{
				if (_innerContext != null)
					_innerContext.BuildQuery(query, queryParameter);
				else
					base.BuildQuery(query, queryParameter);
			}

			public override SqlInfo[] ConvertToSql(Expression expression, int level, ConvertFlags flags)
			{
				if (_innerContext != null)
					return _innerContext.ConvertToSql(expression, level, flags);

				return base.ConvertToSql(expression, level, flags);
			}

			public override SqlInfo[] ConvertToIndex(Expression expression, int level, ConvertFlags flags)
			{
				if (_innerContext != null)
				{
					return _innerContext.ConvertToIndex(expression, level, flags);
				}

				return base.ConvertToIndex(expression, level, flags);
			}

			ISqlExpression CorrectExpression(ISqlExpression expression)
			{
				return new QueryVisitor().Convert(expression, e =>
				{
					if (e.ElementType == QueryElementType.SqlField && _replaceMap.TryGetValue((SqlField)e, out var newField))
						return newField;
					return e;

				}); 
			}

			public override int ConvertToParentIndex(int index, IBuildContext context)
			{
				if (_innerContext != null)
				{
					var column = _innerContext.SelectQuery.Select.Columns[index];
					index = SelectQuery.Select.Add(CorrectExpression(column.Expression));
				}

				return base.ConvertToParentIndex(index, context);
			}

			public override Expression BuildExpression(Expression expression, int level, bool enforceServerSide)
			{
				if (_innerContext != null)
					return _innerContext.BuildExpression(expression, level, enforceServerSide);

				return base.BuildExpression(expression, level, enforceServerSide);
			}

			protected override Expression ProcessExpression(Expression expression)
			{
				var isLeft = false;

				for (
					var association = this;
					isLeft == false && association != null;
					association = association.ParentAssociation as AssociatedTableContext)
				{
					isLeft =
						association.ParentAssociationJoin.JoinType == JoinType.Left ||
						association.ParentAssociationJoin.JoinType == JoinType.OuterApply;
				}

				if (isLeft)
				{
					Expression cond = null;

					var keys = ConvertToIndex(null, 0, ConvertFlags.Key);

					foreach (var key in keys)
					{
						var index2  = ConvertToParentIndex(key.Index, null);

						Expression e = Expression.Call(
							ExpressionBuilder.DataReaderParam,
							ReflectionHelper.DataReader.IsDBNull,
							Expression.Constant(index2));

						cond = cond == null ? e : Expression.AndAlso(cond, e);
					}

					expression = Expression.Condition(cond, Expression.Constant(null, expression.Type), expression);
				}

				return expression;
			}

			protected internal override List<MemberInfo[]> GetLoadWith()
			{
				if (LoadWith == null)
				{
					var loadWith = ParentAssociation.GetLoadWith();

					if (loadWith != null)
					{
						foreach (var item in GetLoadWith(loadWith))
						{
							if (Association.MemberInfo.EqualsTo(item.MemberInfo))
							{
								LoadWith = item.NextLoadWith;
								break;
							}
						}
					}
				}

				return LoadWith;
			}

			interface ISubQueryHelper
			{
				Expression GetSubquery(
					ExpressionBuilder      builder,
					AssociatedTableContext tableContext,
					ParameterExpression    parentObject);
			}

			class SubQueryHelper<T> : ISubQueryHelper
				where T : class
			{
				public Expression GetSubquery(
					ExpressionBuilder      builder,
					AssociatedTableContext tableContext,
					ParameterExpression    parentObject)
				{
					var lContext = Expression.Parameter(typeof(IDataContext), "ctx");
					var lParent  = Expression.Parameter(typeof(object), "parentObject");

					Expression expression;

					var queryMethod = tableContext.Association.GetQueryMethod(parentObject.Type, typeof(T));
					if (queryMethod != null)
					{
						var ownerParam = queryMethod.Parameters[0];
						var dcParam    = queryMethod.Parameters[1];
						var ownerExpr  = Expression.Convert(lParent, parentObject.Type);

						expression = queryMethod.Body.Transform(e =>
							e == ownerParam ? ownerExpr : (e == dcParam ? lContext : e));
					}
					else
					{
						var tableExpression = builder.DataContext.GetTable<T>();

						var loadWith = tableContext.GetLoadWith();

						if (loadWith != null)
						{
							foreach (var members in loadWith)
							{
								var pLoadWith  = Expression.Parameter(typeof(T), "t");
								var isPrevList = false;

								Expression obj = pLoadWith;

								foreach (var member in members)
								{
									if (isPrevList)
										obj = new GetItemExpression(obj);

									obj = Expression.MakeMemberAccess(obj, member);

									isPrevList = typeof(IEnumerable).IsSameOrParentOf(obj.Type);
								}

								tableExpression = tableExpression.LoadWith(Expression.Lambda<Func<T,object>>(obj, pLoadWith));
							}
						}
						
						// Where
						var pWhere = Expression.Parameter(typeof(T), "t");

						Expression expr = null;

						for (var i = 0; i < tableContext.Association.ThisKey.Length; i++)
						{
							var thisProp  = Expression.PropertyOrField(Expression.Convert(lParent, parentObject.Type), tableContext.Association.ThisKey[i]);
							var otherProp = Expression.PropertyOrField(pWhere, tableContext.Association.OtherKey[i]);

							var ex = ExpressionBuilder.Equal(tableContext.Builder.MappingSchema, otherProp, thisProp);

							expr = expr == null ? ex : Expression.AndAlso(expr, ex);
						}

						var predicate = tableContext.Association.GetPredicate(parentObject.Type, typeof(T));
						if (predicate != null)
						{
							var ownerParam = predicate.Parameters[0];
							var childParam = predicate.Parameters[1];
							var ownerExpr  = Expression.Convert(lParent, parentObject.Type);

							var body = predicate.Body.Transform(e =>
								e == ownerParam ? ownerExpr : (e == childParam ? pWhere : e));

							expr = expr == null ? body : Expression.AndAlso(expr, body);
						}

						expression = tableExpression.Where(Expression.Lambda<Func<T,bool>>(expr, pWhere)).Expression;
					}

					var lambda      = Expression.Lambda<Func<IDataContext,object,IEnumerable<T>>>(expression, lContext, lParent);
					var queryReader = CompiledQuery.Compile(lambda);

					expression = Expression.Call(
						null,
						MemberHelper.MethodOf(() => ExecuteSubQuery(null, null, null)),
							ExpressionBuilder.QueryRunnerParam,
							Expression.Convert(parentObject, typeof(object)),
							Expression.Constant(queryReader));

					var memberType = tableContext.Association.MemberInfo.GetMemberType();

					if (memberType == typeof(T[]))
						return Expression.Call(null, MemberHelper.MethodOf(() => Enumerable.ToArray<T>(null)), expression);

					if (memberType.IsSameOrParentOf(typeof(List<T>)))
						return Expression.Call(null, MemberHelper.MethodOf(() => Enumerable.ToList<T>(null)), expression);

					var ctor = memberType.GetConstructor(new[] { typeof(IEnumerable<T>) });

					if (ctor != null)
						return Expression.New(ctor, expression);

					var l = builder.MappingSchema.GetConvertExpression(expression.Type, memberType, false, false);

					if (l != null)
						return l.GetBody(expression);

					throw new LinqToDBException($"Expected constructor '{memberType.Name}(IEnumerable<{tableContext.ObjectType}>)'");
				}

				static IEnumerable<T> ExecuteSubQuery(
					IQueryRunner                             queryRunner,
					object                                   parentObject,
					Func<IDataContext,object,IEnumerable<T>> queryReader)
				{
					using (var db = queryRunner.DataContext.Clone(true))
						foreach (var item in queryReader(db, parentObject))
							yield return item;
				}
			}

			protected override ISqlExpression GetField(Expression expression, int level, bool throwException)
			{
				if (_innerContext != null)
				{
					var infos = _innerContext.ConvertToSql(expression, level, ConvertFlags.Field);
					return infos.FirstOrDefault()?.Sql;
				}

				return base.GetField(expression, level, throwException);
			}

			protected override Expression BuildQuery(Type tableType, TableContext tableContext, ParameterExpression parentObject)
			{
				if (IsList == false)
				{
					if (_innerContext != null)
						return _innerContext.BuildExpression(null, 0, false);
					return base.BuildQuery(tableType, tableContext, parentObject);
				}

				if (Common.Configuration.Linq.AllowMultipleQuery == false)
					throw new LinqException("Multiple queries are not allowed. Set the 'LinqToDB.Common.Configuration.Linq.AllowMultipleQuery' flag to 'true' to allow multiple queries.");

				var sqtype = typeof(SubQueryHelper<>).MakeGenericType(tableType);
				var helper = (ISubQueryHelper)Activator.CreateInstance(sqtype);

				return helper.GetSubquery(Builder, this, parentObject);
			}
		}
	}
}
