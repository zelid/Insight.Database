﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Insight.Database.CodeGenerator
{
	/// <summary>
	/// Generates the IL that is needed to convert a value from an input type to an output type.
	/// This handles automatic conversion to/from nullables, Xml, strings, etc.
	/// </summary>
	[SuppressMessage("Microsoft.StyleCop.CSharp.DocumentationRules", "SA1600:ElementsMustBeDocumented", Justification = "Documenting the internal properties reduces readability without adding additional information.")]
	static class TypeConverterGenerator
	{
		#region Private Members
		internal static readonly MethodInfo CreateDataExceptionMethod = typeof(TypeConverterGenerator).GetMethod("CreateDataException");
		private static readonly MethodInfo _enumParse = typeof(Enum).GetMethod("Parse", new Type[] { typeof(Type), typeof(string), typeof(bool) });
		private static readonly ConstructorInfo _linqBinaryCtor = typeof(System.Data.Linq.Binary).GetConstructor(new Type[] { typeof(byte[]) });
		private static readonly MethodInfo _readChar = typeof(TypeConverterGenerator).GetMethod("ReadChar");
		private static readonly MethodInfo _readNullableChar = typeof(TypeConverterGenerator).GetMethod("ReadNullableChar");
		private static readonly MethodInfo _readXmlDocument = typeof(TypeConverterGenerator).GetMethod("ReadXmlDocument");
		private static readonly MethodInfo _readXDocument = typeof(TypeConverterGenerator).GetMethod("ReadXDocument");
		private static readonly MethodInfo _deserializeXml = typeof(TypeConverterGenerator).GetMethod("DeserializeXml");

		/// <summary>
		/// The number of ticks to offset when converting between .NET TimeSpan and SQL DateTime.
		/// </summary>
		private static readonly long SqlZeroTime = new DateTime(1900, 1, 1, 0, 0, 0).Ticks;
		#endregion

		#region Code Generation Members
		/// <summary>
		/// Emit the IL to convert the current value on the stack and set the value of the object.
		/// </summary>
		/// <param name="il">The IL generator to output to.</param>
		/// <param name="sourceType">The current type of the value.</param>
		/// <param name="method">The set property method to call.</param>
		/// <remarks>
		///	Expects the stack to contain:
		///		Target Object
		///		Value to set
		/// The value is first converted to the type required by the method parameter, then sets the property.
		/// </remarks>
		/// <returns>A label that needs to be marked at the end of a succesful set.</returns>
		public static Label EmitConvertAndSetValue(ILGenerator il, Type sourceType, ClassPropInfo method)
		{
			// targetType - the target type we need to convert to
			// underlyingTargetType - if the target type is nullable, we need to look at the underlying target type
			// rawTargetType - if the underlying target type is enum, we need to look at the underlying target type for that
			// sourceType - this is the type of the data in the data set
			Type targetType = method.MemberType;
			Type underlyingTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

			// some labels that we need
			Label isDbNullLabel = il.DefineLabel();
			Label finishLabel = il.DefineLabel();

			// if the value is DbNull, then we continue to the next item
			il.Emit(OpCodes.Dup);								// dup value, stack => [target][value][value]
			il.Emit(OpCodes.Isinst, typeof(DBNull));			// isinst DBNull:value, stack => [target][value-as-object][DBNull or null]
			il.Emit(OpCodes.Brtrue_S, isDbNullLabel);			// br.true isDBNull, stack => [target][value-as-object]

			// handle the special target types first
			if (targetType == typeof(char))
			{
				// char
				il.EmitCall(OpCodes.Call, _readChar, null);
			}
			else if (targetType == typeof(char?))
			{
				// char?
				il.EmitCall(OpCodes.Call, _readNullableChar, null);
			}
			else if (targetType == typeof(System.Data.Linq.Binary))
			{
				// unbox sql byte arrays to Linq.Binary

				// before: stack => [target][object-value]
				// after: stack => [target][byte-array-value]
				il.Emit(OpCodes.Unbox_Any, typeof(byte[])); // stack is now [target][byte-array]
				// before: stack => [target][byte-array-value]
				// after: stack => [target][Linq.Binary-value]
				il.Emit(OpCodes.Newobj, _linqBinaryCtor);
			}
			else if (targetType == typeof(XmlDocument))
			{
				// special handler for XmlDocuments

				// before: stack => [target][object-value]
				il.Emit(OpCodes.Call, _readXmlDocument);

				// after: stack => [target][xmlDocument]
			}
			else if (targetType == typeof(XDocument))
			{
				// special handler for XDocuments

				// before: stack => [target][object-value]
				il.Emit(OpCodes.Call, _readXDocument);

				// after: stack => [target][xDocument]
			}
			else if (sourceType == typeof(string) && targetType != typeof(string) && !targetType.IsValueType)
			{
				// we are getting a string from the database, but the target is not a string, but it's a reference type
				// assume the column is an xml data type and that we want to deserialize it

				// before: stack => [target][object-value]
				il.EmitLoadType(targetType);

				// after: stack => [target][object-value][memberType]
				il.Emit(OpCodes.Call, _deserializeXml);
				il.Emit(OpCodes.Unbox_Any, targetType);
			}
			else if (underlyingTargetType.IsEnum && sourceType == typeof(string))
			{
				var localString = il.DeclareLocal(typeof(string));

				// if we are converting a string to an enum, then parse it.
				// see if the value from the database is a string. if so, we need to parse it. If not, we will just try to unbox it.
				il.Emit(OpCodes.Isinst, typeof(string));			// is string, stack => [target][string]
				il.Emit(OpCodes.Stloc, localString);				// pop loc.2 (enum), stack => [target]

				// call enum.parse (type, value, true)
				il.EmitLoadType(underlyingTargetType);
				il.Emit(OpCodes.Ldloc, localString);				// push enum, stack => [target][enum-type][string]
				il.Emit(OpCodes.Ldc_I4_1);							// push true, stack => [target][enum-type][string][true]
				il.EmitCall(OpCodes.Call, _enumParse, null);		// call Enum.Parse, stack => [target][enum-as-object]

				// Enum.Parse returns an object, which we need to unbox to the enum value
				il.Emit(OpCodes.Unbox_Any, underlyingTargetType);
			}
			else
			{
				// this isn't a system value type, so unbox to the type the reader is giving us (this is a system type, hopefully)
				// now we have an unboxed sourceType
				il.Emit(OpCodes.Unbox_Any, sourceType);

				// look for a constructor that takes the type as a parameter
				Type[] sourceTypes = new Type[] { sourceType };
				ConstructorInfo ci = targetType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, sourceTypes, null);
				if (ci != null)
				{
					// if the constructor only takes nullable types, warn the programmer
					if (Nullable.GetUnderlyingType(ci.GetParameters()[0].ParameterType) != null)
						throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture, "Class {0} must provide a constructor taking a parameter of type {1}. Nullable parameters are not supported.", targetType, sourceType));

					il.Emit(OpCodes.Newobj, ci);
				}
				else
				{
					// attempt to convert the value to the target type
					if (!EmitConversionOrCoersion(il, sourceType, targetType))
					{
						if (sourceType != targetType)
						{
							throw new InvalidOperationException(String.Format(
								CultureInfo.InvariantCulture,
								"Field {0} cannot be converted from {1} to {2}. Create a conversion constructor or conversion operator.",
								method.Name,
								sourceType,
								targetType));
						}
					}

					// if the target is nullable, then construct the nullable from the data
					if (Nullable.GetUnderlyingType(targetType) != null)
						il.Emit(OpCodes.Newobj, targetType.GetConstructor(new[] { underlyingTargetType }));
				}
			}

			/////////////////////////////////////////////////////////////////////
			// now the stack has [target][value-unboxed]. we can set the value now
			method.EmitSetValue(il);

			// stack is now EMPTY
			/////////////////////////////////////////////////////////////////////

			/////////////////////////////////////////////////////////////////////
			// jump over our DBNull handler
			il.Emit(OpCodes.Br_S, finishLabel);
			/////////////////////////////////////////////////////////////////////

			/////////////////////////////////////////////////////////////////////
			// cleanup after IsDBNull.
			/////////////////////////////////////////////////////////////////////
			il.MarkLabel(isDbNullLabel);							// stack => [target][value]
			il.Emit(OpCodes.Pop);									// pop value, stack => [target]

			// if the type is an object, set the value to null
			// this is necessary for overwriting output parameters,
			// as well as overwriting any properties that may be set in the constructor of the object
			if (!method.MemberType.IsValueType)
			{
				il.Emit(OpCodes.Ldnull);							// push null
				method.EmitSetValue(il);
			}
			else
			{
				// we didn't call setvalue, so pop the target object off the stack
				il.Emit(OpCodes.Pop);								// pop target, stack => [empty]
			}

			return finishLabel;
		}
		#endregion

		#region Helper Methods
		/// <summary>
		/// Wrap an exception with a DataException that contains more information.
		/// </summary>
		/// <param name="ex">The inner exception.</param>
		/// <param name="index">The index of the column.</param>
		/// <param name="reader">The data reader.</param>
		/// <param name="value">The value read from the reader.</param>
		/// <returns>An exception that can be thrown.</returns>
		public static Exception CreateDataException(Exception ex, int index, IDataReader reader, object value)
		{
			string name = "n/a";

			if (reader != null && !reader.IsClosed && index >= 0 && index < reader.FieldCount)
			{
				name = reader.GetName(index);
				if (value == null || value is DBNull)
					value = "<null>";
				else
					value = value.ToString() + " - " + Type.GetTypeCode(value.GetType());
			}

			return new DataException(string.Format(CultureInfo.InvariantCulture, "Error parsing column {0} ({1}={2})", index, name, value), ex);
		}

		/// <summary>
		/// Convert an object value to a char.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <returns>A single character.</returns>
		public static char ReadChar(object value)
		{
			if (value == null || value is DBNull)
				throw new ArgumentNullException("value");

			string s = value as string;
			if (s == null || s.Length != 1)
				throw new ArgumentException("A single character was expected", "value");
			return s[0];
		}

		/// <summary>
		/// Convert an object value to a nullable char.
		/// </summary>
		/// <param name="value">The value to convert.</param>
		/// <returns>A single character.</returns>
		public static char? ReadNullableChar(object value)
		{
			if (value == null || value is DBNull)
				return null;

			string s = value as string;
			if (s == null || s.Length != 1)
				throw new ArgumentException("A single character was expected", "value");

			return s[0];
		}

		/// <summary>
		/// Reads an XmlDocument from a column.
		/// </summary>
		/// <param name="value">The value to convert to an XmlDocument.</param>
		/// <returns>The XmlDocument.</returns>
		public static XmlDocument ReadXmlDocument(object value)
		{
			XmlDocument doc = new XmlDocument();
			doc.LoadXml(value.ToString());
			return doc;
		}

		/// <summary>
		/// Reads an XDocument from a column.
		/// </summary>
		/// <param name="value">The value to convert to an XDocument.</param>
		/// <returns>The XDocument.</returns>
		public static XDocument ReadXDocument(object value)
		{
			return XDocument.Parse(value.ToString(), LoadOptions.None);
		}

		/// <summary>
		/// Reads an object from an Xml column.
		/// </summary>
		/// <param name="value">The value to deserialize.</param>
		/// <param name="type">The type to deserialize to.</param>
		/// <returns>The deserialized object.</returns>
		public static object DeserializeXml(object value, Type type)
		{
			DataContractSerializer serializer = new DataContractSerializer(type);

			StringReader reader = new StringReader(value.ToString());
			try
			{
				using (XmlTextReader xr = new XmlTextReader(reader))
				{
					reader = null;
					return serializer.ReadObject(xr);
				}
			}
			finally
			{
				if (reader != null)
					reader.Dispose();
			}
		}
		#endregion

		#region TimeSpan Helpers
		/// <summary>
		/// Converts a DateTime from the SQL side into a .NET TimeSpan by offseting by SqlZeroTime.
		/// </summary>
		/// <param name="dateTime">The DateTime to convert.</param>
		/// <returns>The corresponding TimeSpan.</returns>
		public static TimeSpan SqlDateTimeToTimeSpan(DateTime dateTime)
		{
			return new TimeSpan(dateTime.Ticks - SqlZeroTime);
		}

		/// <summary>
		/// Converts a .NET TimeSpan to a SQL DateTime by offseting by SqlZeroTime.
		/// </summary>
		/// <param name="span">The TimeSpan to convert.</param>
		/// <returns>The corresponding SQL DateTime.</returns>
		public static DateTime TimeSpanToSqlDateTime(TimeSpan span)
		{
			return new DateTime(span.Ticks + SqlZeroTime);
		}

		/// <summary>
		/// Converts a .NET TimeSpan to a SQL DateTime by offseting by SqlZeroTime.
		/// </summary>
		/// <param name="span">The TimeSpan to convert.</param>
		/// <returns>The corresponding SQL DateTime.</returns>
		public static DateTime? TimeSpanToNullableSqlDateTime(TimeSpan? span)
		{
			if (span == null)
				return (DateTime?)null;

			return TimeSpanToSqlDateTime(span.Value);
		}

		/// <summary>
		/// Converts a .NET TimeSpan to a SQL DateTime by offseting by SqlZeroTime.
		/// The object is only offset if it is a TimeSpan or TimeSpan?.
		/// When converting to a SQL time, the value must be within a 24-hour period.
		/// </summary>
		/// <param name="o">The object to convert.</param>
		/// <param name="dbType">The expected type in the database..</param>
		/// <returns>The corresponding SQL DateTime.</returns>
		public static object ObjectToSqlDateTime(object o, DbType dbType)
		{
			if (o == null)
				return null;

			if (dbType == DbType.Time)
				return o;

			if (o is TimeSpan)
			{
				TimeSpan timeSpan = (TimeSpan)o;

				// if we are converting to a timespan, make sure it is within the range of one day
				if (dbType == DbType.Time && (timeSpan.Ticks < 0 || timeSpan.Ticks >= TimeSpan.TicksPerDay))
					throw new InvalidOperationException("Error converting timespan to time. Value must be between 0 and 1 day.");

				return TimeSpanToSqlDateTime(timeSpan);
			}
			else if (o is TimeSpan?)
			{
				TimeSpan timeSpan = ((TimeSpan?)o).Value;

				// if we are converting to a timespan, make sure it is within the range of one day
				if (dbType == DbType.Time && (timeSpan.Ticks < 0 || timeSpan.Ticks >= TimeSpan.TicksPerDay))
					throw new InvalidOperationException("Error converting timespan to time. Value must be between 0 and 1 day.");

				return TimeSpanToNullableSqlDateTime((TimeSpan?)o);
			}

			// We don't know how to convert it. Let .NET handle it.
			return o;
		}

		/// <summary>
		/// Converts a SQL DateTime  to a .NET TimeSpan by offseting by SqlZeroTime.
		/// The object is only offset if it is a DateTime.
		/// </summary>
		/// <param name="o">The object to convert.</param>
		/// <returns>The corresponding .NET TimeSpan.</returns>
		public static object SqlObjectToTimeSpan(object o)
		{
			if (o == null)
				return null;

			if (o is DateTime)
				return SqlDateTimeToTimeSpan((DateTime)o);

			// We don't know how to convert it. Let .NET handle it.
			return o;
		}
		#endregion

		#region Code Generation Helpers
		/// <summary>
		/// Emit a conversion or coersion from the source type to the target type.
		/// </summary>
		/// <param name="il">The IL generator to use.</param>
		/// <param name="sourceType">The source type.</param>
		/// <param name="targetType">The target type.</param>
		/// <returns>True if a conversion was emitted, false if one could not be found.</returns>
		internal static bool EmitConversionOrCoersion(ILGenerator il, Type sourceType, Type targetType)
		{
			if (TypeConverterGenerator.EmitConversion(il, sourceType, targetType))
				return true;

			Type underlyingTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
			Type rawTargetType = underlyingTargetType.IsEnum ? Enum.GetUnderlyingType(underlyingTargetType) : underlyingTargetType;

			return TypeConverterGenerator.EmitCoersion(il, sourceType, rawTargetType);
		}

		/// <summary>
		/// Emit a conversion from the source type to the target type.
		/// </summary>
		/// <param name="il">The IL generator to use.</param>
		/// <param name="sourceType">The source type.</param>
		/// <param name="targetType">The target type.</param>
		/// <returns>True if a conversion was emitted, false if one could not be found.</returns>
		private static bool EmitConversion(ILGenerator il, Type sourceType, Type targetType)
		{
			MethodInfo mi = FindConversionMethod(sourceType, targetType);
			if (mi == null)
				return false;

			il.Emit(mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
			return true;
		}

		/// <summary>
		/// Attempt to find a valid conversion method.
		/// </summary>
		/// <param name="sourceType">The source type for the conversion.</param>
		/// <param name="targetType">The target type for the conversion.</param>
		/// <returns>A conversion method or null if none could be found.</returns>
		private static MethodInfo FindConversionMethod(Type sourceType, Type targetType)
		{
			// if the types match, then there is no conversion
			if (sourceType == targetType)
				return null;

			// look at conversion operators
			MethodInfo mi =
				FindConversionMethod("op_Explicit", targetType, sourceType, targetType) ??
				FindConversionMethod("op_Implicit", targetType, sourceType, targetType) ??
				FindConversionMethod("op_Explicit", sourceType, sourceType, targetType) ??
				FindConversionMethod("op_Implicit", sourceType, sourceType, targetType);
			if (mi != null)
				return mi;

			// if the target type is an enum or nullable, try converting to one of those
			if (Nullable.GetUnderlyingType(targetType) != null)
				return FindConversionMethod(sourceType, Nullable.GetUnderlyingType(targetType));
			if (targetType.IsEnum)
				return FindConversionMethod(sourceType, Enum.GetUnderlyingType(targetType));

			// handle converting sql datetime to timespan
			if (sourceType == typeof(DateTime) && targetType == typeof(TimeSpan))
				return typeof(TypeConverterGenerator).GetMethod("SqlDateTimeToTimeSpan");

			return null;
		}

		/// <summary>
		/// Look up a conversion method from a type.
		/// </summary>
		/// <param name="methodName">The name of the method to find.</param>
		/// <param name="searchType">The type to search through.</param>
		/// <param name="sourceType">The source type for the conversion.</param>
		/// <param name="targetType">The target type for the conversion.</param>
		/// <returns>A conversion method or null if none could be found.</returns>
		private static MethodInfo FindConversionMethod(string methodName, Type searchType, Type sourceType, Type targetType)
		{
			var members = searchType.FindMembers(
				MemberTypes.Method,
				BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				new MemberFilter(
				(_m, filter) =>
				{
					MethodInfo m = _m as MethodInfo;
					if (m.Name != methodName) return false;
					if (m.ReturnType != targetType) return false;
					ParameterInfo[] pi = m.GetParameters();
					if (pi.Length != 1) return false;
					if (pi[0].ParameterType != sourceType) return false;
					return true;
				}),
				null);

			return (MethodInfo)members.FirstOrDefault();
		}

		/// <summary>
		/// Assuming the source and target types are primitives, coerce the types and handle nullable conversions.
		/// </summary>
		/// <param name="il">The il generator.</param>
		/// <param name="sourceType">The source type of data.</param>
		/// <param name="targetType">The type to coerce to.</param>
		/// <returns>True if a coersion was emitted, false otherwise.</returns>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
		private static bool EmitCoersion(ILGenerator il, Type sourceType, Type targetType)
		{
			// support auto-converting strings to other types by parsing
			if (sourceType == typeof(string))
			{
				if (targetType == typeof(TimeSpan) || targetType == typeof(DateTime) || targetType == typeof(DateTimeOffset))
				{
					il.Emit(OpCodes.Call, targetType.GetMethod("Parse", new Type[] { typeof(string) }));
					return true;
				}
			}

			// if we are converting to a string, use the default ToString on the object
			if (targetType == typeof(string))
			{
				IlHelper.EmitToStringOrNull(il, sourceType);
				return true;
			}

			if (!sourceType.IsPrimitive) return false;
			if (!targetType.IsPrimitive) return false;

			// if the enum is based on a different type of integer than returned, then do the conversion
			if (targetType == typeof(Int32) && sourceType != typeof(Int32)) il.Emit(OpCodes.Conv_I4);
			else if (targetType == typeof(Int64) && sourceType != typeof(Int64)) il.Emit(OpCodes.Conv_I8);
			else if (targetType == typeof(Int16) && sourceType != typeof(Int16)) il.Emit(OpCodes.Conv_I2);
			else if (targetType == typeof(char) && sourceType != typeof(char)) il.Emit(OpCodes.Conv_I1);
			else if (targetType == typeof(sbyte) && sourceType != typeof(sbyte)) il.Emit(OpCodes.Conv_I1);
			else if (targetType == typeof(UInt32) && sourceType != typeof(UInt32)) il.Emit(OpCodes.Conv_U4);
			else if (targetType == typeof(UInt64) && sourceType != typeof(UInt64)) il.Emit(OpCodes.Conv_U8);
			else if (targetType == typeof(UInt16) && sourceType != typeof(UInt16)) il.Emit(OpCodes.Conv_U2);
			else if (targetType == typeof(byte) && sourceType != typeof(byte)) il.Emit(OpCodes.Conv_U1);
			else if (targetType == typeof(bool) && sourceType != typeof(bool)) il.Emit(OpCodes.Conv_U1);
			else if (targetType == typeof(double) && sourceType != typeof(double)) il.Emit(OpCodes.Conv_R8);
			else if (targetType == typeof(float) && sourceType != typeof(float)) il.Emit(OpCodes.Conv_R4);

			return true;
		}
		#endregion
	}
}
