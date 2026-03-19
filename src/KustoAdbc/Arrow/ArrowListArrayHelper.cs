using System.Collections.Generic;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace KustoAdbc
{
    /// <summary>
    /// Builds a ListArray from a list of nullable child StructArrays.
    /// Replicates the pattern from the BigQuery ADBC driver.
    /// </summary>
    static class ArrowListArrayHelper
    {
        public static ListArray BuildListArray(
            IReadOnlyList<IArrowArray?> items,
            IArrowType elementType)
        {
            var offsetsBuilder = new ArrowBuffer.Builder<int>();
            var validityBuilder = new ArrowBuffer.BitmapBuilder();
            var childDataList = new List<ArrayData>();
            int totalLength = 0;
            int nullCount = 0;

            foreach (var item in items)
            {
                offsetsBuilder.Append(totalLength);
                if (item == null)
                {
                    validityBuilder.Append(false);
                    nullCount++;
                }
                else
                {
                    validityBuilder.Append(true);
                    childDataList.Add(item.Data);
                    totalLength += item.Length;
                }
            }
            offsetsBuilder.Append(totalLength);

            ArrowBuffer validityBuffer = nullCount > 0 ? validityBuilder.Build() : ArrowBuffer.Empty;

            // Concatenate all child array data, or create an empty array if none
            IArrowArray childArray;
            if (childDataList.Count > 0)
            {
                var arrowArrays = new List<IArrowArray>(childDataList.Count);
                foreach (var data in childDataList)
                    arrowArrays.Add(ArrowArrayFactory.BuildArray(data));
                childArray = ArrowArrayConcatenator.Concatenate(arrowArrays);
            }
            else
            {
                childArray = CreateEmptyArray(elementType);
            }

            return new ListArray(
                new ListType(new Field("item", elementType, true)),
                items.Count,
                offsetsBuilder.Build(),
                childArray,
                validityBuffer,
                nullCount);
        }

        static IArrowArray CreateEmptyArray(IArrowType type)
        {
            if (type is StructType structType)
            {
                var emptyFields = new IArrowArray[structType.Fields.Count];
                for (int i = 0; i < structType.Fields.Count; i++)
                {
                    emptyFields[i] = CreateEmptyArray(structType.Fields[i].DataType);
                }
                return new StructArray(structType, 0, emptyFields, ArrowBuffer.Empty);
            }
            if (type is ListType listType)
            {
                var emptyChild = CreateEmptyArray(listType.ValueDataType);
                return new ListArray(
                    listType, 0,
                    new ArrowBuffer.Builder<int>().Append(0).Build(),
                    emptyChild,
                    ArrowBuffer.Empty);
            }
            if (type is StringType)
            {
                return new StringArray.Builder().Build();
            }
            if (type is Int32Type)
            {
                return new Int32Array.Builder().Build();
            }
            if (type is Int16Type)
            {
                return new Int16Array.Builder().Build();
            }
            if (type is BooleanType)
            {
                return new BooleanArray.Builder().Build();
            }
            if (type is UInt32Type)
            {
                return new UInt32Array.Builder().Build();
            }

            // Fallback: empty string array
            return new StringArray.Builder().Build();
        }

        /// <summary>
        /// Concatenates multiple arrays of the same type into one.
        /// </summary>
        static IArrowArray ArrowArrayConcatenator_Concatenate(IReadOnlyList<ArrayData> arrays)
        {
            // For a single array, just build directly
            if (arrays.Count == 1)
                return ArrowArrayFactory.BuildArray(arrays[0]);

            // Use Apache Arrow's built-in concatenation
            var arrowArrays = new List<IArrowArray>(arrays.Count);
            foreach (var data in arrays)
                arrowArrays.Add(ArrowArrayFactory.BuildArray(data));

            return Apache.Arrow.ArrowArrayConcatenator.Concatenate(arrowArrays);
        }
    }
}
