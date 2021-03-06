﻿using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using XtlSharp.Native;

namespace XtlSharp
{
    public partial class XArray : IDisposable
    {
        /// <summary>
        /// The sizes
        /// </summary>
        public Shape Shape { get; set; }

        public DType DataType
        {
            get
            {
                return dtype;
            }
        }

        /// <summary>
        /// The strides
        /// </summary> 
        private long[] strides;
       
        /// <summary>
        /// The storage offset
        /// </summary>
        private long storageOffset;

        /// <summary>
        /// The is disposed
        /// </summary>
        private bool isDisposed = false;

        private readonly DType dtype;

        public IntPtr NativePtr
        {
            get;
            set;

        }

        public XArray(Array data)
        {
            GCHandle gch = GCHandle.Alloc(data, GCHandleType.Pinned);
            NativePtr = gch.AddrOfPinnedObject();
            gch.Free();

            long[] shape = new long[data.Rank];
            for (int i = 0; i < shape.Length; i++)
                shape[i] = data.GetLength(i);

            dtype = DTypeBuilder.FromCLRType(data.GetType());
            Shape = new Shape(shape);
            strides = GetContiguousStride(Shape.Data);
        }

        public XArray(Shape shape, DType dtype = DType.Float32)
        {
            Shape = shape;
            this.dtype = dtype;
            strides = GetContiguousStride(Shape.Data);
            long byteSize = dtype.Size() * shape.Size;
            NativePtr = Marshal.AllocHGlobal(new IntPtr(byteSize));
        }

        internal XArray(long[] sizes, long[] strides, IntPtr ptr, DType dtype)
        {
            this.Shape = new Shape(sizes);
            this.strides = strides;
            this.NativePtr = ptr;
            this.dtype = dtype;
        }

        private static long[] GetContiguousStride(long[] dims)
        {
            long acc = 1;
            var stride = new long[dims.Length];
            for (int i = dims.Length - 1; i >= 0; --i)
            {
                stride[i] = acc;
                acc *= dims[i];
            }

            return stride;
        }

        public Array ToArray()
        {
            long arraySize = Shape.Size;

            Array result = Array.CreateInstance(DataType.ToCLRType(), Shape.Data);
            var bytecount = DataType.Size() * arraySize;
            var datagch = GCHandle.Alloc(result, GCHandleType.Pinned);
            unsafe
            {
                Buffer.MemoryCopy(NativePtr.ToPointer(), datagch.AddrOfPinnedObject().ToPointer(), bytecount, bytecount);
            }

            datagch.Free();

            return result;
        }

        public bool IsContiguous()
        {
            long z = 1;
            for (int d = Shape.Dimension - 1; d >= 0; d--)
            {
                if (Shape[d] != 1)
                {
                    if (strides[d] == z)
                        z *= Shape[d];
                    else
                        return false;
                }
            }
            return true;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(NativePtr);
        }

        private XArray View(params long[] sizes)
        {
            if (!this.IsContiguous()) throw new InvalidOperationException("Cannot use View on a non-contiguous tensor");

            Shape new_shape = new Shape(sizes);
            if (this.Shape.Size != new_shape.Size)
            {
                throw new InvalidOperationException("Output tensor must have the same number of elements as the input");
            }

            return new XArray(sizes, GetContiguousStride(sizes), this.NativePtr, this.dtype);
        }

        public XArray Reshape(params long[] sizes)
        {
            if (!this.IsContiguous()) throw new InvalidOperationException("Cannot use View on a non-contiguous tensor");

            Shape new_shape = new Shape(sizes);
            long prod = -1 * new_shape.Size;
            for (int i = 0; i < sizes.Length; i++)
            {
                if (sizes[i] == -1)
                {
                    sizes[i] = Shape.Size / prod;
                    break;
                }
            }

            return View(sizes);
        }

        public XArray Ravel()
        {
            return View(Shape.Size);
        }

        public void Fill(double value)
        {
            NativeHelper.Fill(this, value);
        }

        public XArray Slice(int dimension, long startIndex, long size)
        {
            if (dimension < 0 || dimension >= Shape.Dimension) throw new ArgumentOutOfRangeException("dimension");
            if (startIndex < 0 || startIndex >= Shape[dimension]) throw new ArgumentOutOfRangeException("startIndex");
            if (size <= 0 || startIndex + size > Shape[dimension]) throw new ArgumentOutOfRangeException("size");

            var newOffset = (storageOffset + startIndex * strides[dimension]) * DataType.Size();
            var newSizes = Shape.Data;
            newSizes[dimension] = size;
            var n = new IntPtr(NativePtr.ToInt64() + newOffset);
            return new XArray(newSizes, strides, n, this.dtype);
        }

        private double GetValue(long index)
        {
            unsafe
            {
                if (DataType == DType.Float32) return ((float*)NativePtr.ToPointer())[index];
                else if (DataType == DType.Float64) return ((double*)NativePtr.ToPointer())[index];
                else if (DataType == DType.Int32) return ((int*)NativePtr.ToPointer())[index];
                else if (DataType == DType.UInt8) return ((byte*)NativePtr.ToPointer())[index];
                else
                    throw new NotSupportedException("Element type " + DataType + " not supported");
            }
        }

        private void SetValue(long index, double value)
        {
            unsafe
            {
                if (DataType == DType.Float32) ((float*)NativePtr.ToPointer())[index] = Convert.ToSingle(value);
                else if (DataType == DType.Float64) ((double*)NativePtr.ToPointer())[index] = value;
                else if (DataType == DType.Int32) ((int*)NativePtr.ToPointer())[index] = (int)value;
                else if (DataType == DType.UInt8) ((byte*)NativePtr.ToPointer())[index] = (byte)value;
                else
                    throw new NotSupportedException("Element type " + DataType + " not supported");
            }
        }

        public double this[long index]
        {
            get
            {
                return GetValue(index);
            }
            set
            {
                SetValue(index, value);
            }
        }

        public XArray this[params object[] items]
        {
            get
            {
                return this;
            }
            set
            {
                this = value;
            }
        }

        internal static XArray FromRef(XArrayRef tensorRef)
        {
            long[] shape_data = new long[tensorRef.dimCount];
            Marshal.Copy(tensorRef.sizes, shape_data, 0, shape_data.Length);
            Shape shape = new Shape(shape_data);
            XArray result = new XArray(shape, tensorRef.elementType);
            result.NativePtr = tensorRef.buffer;

            return result;
        }

        public IntPtr GetRef()
        {
            var tensorRef = AllocTensorRef(this);
            var tensorPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(XArrayRef)));
            Marshal.StructureToPtr(tensorRef, tensorPtr, false);

            return tensorPtr;
        }

        internal XArrayRef AllocTensorRef(XArray tensor)
        {
            var tensorRef = new XArrayRef();
            tensorRef.buffer = GetBufferStart(tensor);
            tensorRef.dimCount = tensor.Shape.Length;
            tensorRef.sizes = AllocArray(tensor.Shape.Data);
            tensorRef.strides = AllocArray(tensor.strides);
            tensorRef.elementType = tensor.DataType;
            return tensorRef;
        }

        private IntPtr AllocArray(long[] data)
        {
            var result = Marshal.AllocHGlobal(sizeof(long) * data.Length);
            Marshal.Copy(data, 0, result, data.Length);
            return result;
        }

        /// <summary>
        /// Frees the tensor reference.
        /// </summary>
        /// <param name="tensorRef">The tensor reference.</param>
        internal void FreeTensorRef(XArrayRef tensorRef)
        {
            Marshal.FreeHGlobal(tensorRef.sizes);
            Marshal.FreeHGlobal(tensorRef.strides);
        }

        public IntPtr GetBufferStart(XArray tensor)
        {
            return PtrAdd(NativePtr, tensor.storageOffset * tensor.DataType.Size());
        }

        /// <summary>
        /// PTRs the add.
        /// </summary>
        /// <param name="ptr">The PTR.</param>
        /// <param name="offset">The offset.</param>
        /// <returns>IntPtr.</returns>
        private IntPtr PtrAdd(IntPtr ptr, long offset)
        {
            return new IntPtr(ptr.ToInt64() + offset);
        }

        #region Operators
        public static XArray operator +(XArray lhs, XArray rhs)
        {
            return xt.Add(lhs, rhs);
        }

        public static XArray operator +(XArray lhs, double rhs)
        {
            XArray rhs_t = new XArray(lhs.Shape, lhs.DataType);
            rhs_t.Fill(rhs);
            return lhs + rhs_t;
        }

        public static XArray operator +(double lhs, XArray rhs)
        {
            XArray lhs_t = new XArray(rhs.Shape, rhs.DataType);
            lhs_t.Fill(lhs);
            return lhs_t + rhs;
        }

        public static XArray operator -(XArray lhs, XArray rhs)
        {
            return xt.Sub(lhs, rhs);
        }

        public static XArray operator -(XArray lhs, double rhs)
        {
            XArray rhs_t = new XArray(lhs.Shape, lhs.DataType);
            rhs_t.Fill(rhs);
            return lhs - rhs_t;
        }

        public static XArray operator -(double lhs, XArray rhs)
        {
            XArray lhs_t = new XArray(rhs.Shape, rhs.DataType);
            lhs_t.Fill(lhs);
            return lhs_t - rhs;
        }

        public static XArray operator *(XArray lhs, XArray rhs)
        {
            return xt.Mul(lhs, rhs);
        }

        public static XArray operator *(XArray lhs, double rhs)
        {
            XArray rhs_t = new XArray(lhs.Shape, lhs.DataType);
            rhs_t.Fill(rhs);
            return lhs * rhs_t;
        }

        public static XArray operator *(double lhs, XArray rhs)
        {
            XArray lhs_t = new XArray(rhs.Shape, rhs.DataType);
            lhs_t.Fill(lhs);
            return lhs_t * rhs;
        }

        public static XArray operator /(XArray lhs, XArray rhs)
        {
            return xt.Div(lhs, rhs);
        }

        public static XArray operator /(XArray lhs, double rhs)
        {
            XArray rhs_t = new XArray(lhs.Shape, lhs.DataType);
            rhs_t.Fill(rhs);
            return lhs / rhs_t;
        }

        public static XArray operator /(double lhs, XArray rhs)
        {
            XArray lhs_t = new XArray(rhs.Shape, rhs.DataType);
            lhs_t.Fill(lhs);
            return lhs_t / rhs;
        }

       
        #endregion
    }
}
