using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Matrix.RustSdk.Bindings;

// This is a helper for safely working with byte buffers returned from the Rust code.
// A rust-owned buffer is represented by its capacity, its current length, and a
// pointer to the underlying data.

[StructLayout(LayoutKind.Sequential)]
internal struct RustBuffer
{
    public int capacity;
    public int len;
    public IntPtr data;

    public static RustBuffer Alloc(int size)
    {
        return _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                var buffer = _UniFFILib.ffi_matrix_sdk_ffi_397b_rustbuffer_alloc(size, ref status);
                if (buffer.data == IntPtr.Zero)
                {
                    throw new AllocationException($"RustBuffer.Alloc() returned null data pointer (size={size})");
                }
                return buffer;
            }
        );
    }

    public static void Free(RustBuffer buffer)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_rustbuffer_free(buffer, ref status);
            }
        );
    }

    public BigEndianStream AsStream()
    {
        unsafe
        {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), len));
        }
    }

    public BigEndianStream AsWriteableStream()
    {
        unsafe
        {
            return new BigEndianStream(
                new UnmanagedMemoryStream((byte*)data.ToPointer(), capacity, capacity, FileAccess.Write)
            );
        }
    }
}

// This is a helper for safely passing byte references into the rust code.
// It's not actually used at the moment, because there aren't many things that you
// can take a direct pointer to managed memory, and if we're going to copy something
// then we might as well copy it into a `RustBuffer`. But it's here for API
// completeness.

[StructLayout(LayoutKind.Sequential)]
internal struct ForeignBytes
{
    public int length;
    public IntPtr data;
}

// The FfiConverter interface handles converter types to and from the FFI
//
// All implementing objects should be public to support external types.  When a
// type is external we need to import it's FfiConverter.
internal abstract class FfiConverter<CsType, FfiType>
{
    // Convert an FFI type to a C# type
    public abstract CsType Lift(FfiType value);

    // Convert C# type to an FFI type
    public abstract FfiType Lower(CsType value);

    // Read a C# type from a `ByteBuffer`
    public abstract CsType Read(BigEndianStream stream);

    // Calculate bytes to allocate when creating a `RustBuffer`
    //
    // This must return at least as many bytes as the write() function will
    // write. It can return more bytes than needed, for example when writing
    // Strings we can't know the exact bytes needed until we the UTF-8
    // encoding, so we pessimistically allocate the largest size possible (3
    // bytes per codepoint).  Allocating extra bytes is not really a big deal
    // because the `RustBuffer` is short-lived.
    public abstract int AllocationSize(CsType value);

    // Write a C# type to a `ByteBuffer`
    public abstract void Write(CsType value, BigEndianStream stream);

    // Lower a value into a `RustBuffer`
    //
    // This method lowers a value into a `RustBuffer` rather than the normal
    // FfiType.  It's used by the callback interface code.  Callback interface
    // returns are always serialized into a `RustBuffer` regardless of their
    // normal FFI type.
    public RustBuffer LowerIntoRustBuffer(CsType value)
    {
        var rbuf = RustBuffer.Alloc(AllocationSize(value));
        try
        {
            var stream = rbuf.AsWriteableStream();
            Write(value, stream);
            rbuf.len = Convert.ToInt32(stream.Position);
            return rbuf;
        }
        catch
        {
            RustBuffer.Free(rbuf);
            throw;
        }
    }

    // Lift a value from a `RustBuffer`.
    //
    // This here mostly because of the symmetry with `lowerIntoRustBuffer()`.
    // It's currently only used by the `FfiConverterRustBuffer` class below.
    protected CsType LiftFromRustBuffer(RustBuffer rbuf)
    {
        var stream = rbuf.AsStream();
        try
        {
            var item = Read(stream);
            if (stream.HasRemaining())
            {
                throw new InternalException("junk remaining in buffer after lifting, something is very wrong!!");
            }
            return item;
        }
        finally
        {
            RustBuffer.Free(rbuf);
        }
    }
}

// FfiConverter that uses `RustBuffer` as the FfiType
internal abstract class FfiConverterRustBuffer<CsType> : FfiConverter<CsType, RustBuffer>
{
    public override CsType Lift(RustBuffer value)
    {
        return LiftFromRustBuffer(value);
    }

    public override RustBuffer Lower(CsType value)
    {
        return LowerIntoRustBuffer(value);
    }
}

// A handful of classes and functions to support the generated data structures.
// This would be a good candidate for isolating in its own ffi-support lib.
// Error runtime.
[StructLayout(LayoutKind.Sequential)]
struct RustCallStatus
{
    public int code;
    public RustBuffer error_buf;

    public bool IsSuccess()
    {
        return code == 0;
    }

    public bool IsError()
    {
        return code == 1;
    }

    public bool IsPanic()
    {
        return code == 2;
    }
}

// Base class for all uniffi exceptions
public class UniffiException : Exception
{
    public UniffiException()
        : base() { }

    public UniffiException(string message)
        : base(message) { }
}

public class UndeclaredErrorException : UniffiException
{
    public UndeclaredErrorException(string message)
        : base(message) { }
}

public class PanicException : UniffiException
{
    public PanicException(string message)
        : base(message) { }
}

public class AllocationException : UniffiException
{
    public AllocationException(string message)
        : base(message) { }
}

public class InternalException : UniffiException
{
    public InternalException(string message)
        : base(message) { }
}

public class InvalidEnumException : InternalException
{
    public InvalidEnumException(string message)
        : base(message) { }
}

// Each top-level error class has a companion object that can lift the error from the call status's rust buffer
interface CallStatusErrorHandler<E>
    where E : Exception
{
    E Lift(RustBuffer error_buf);
}

// CallStatusErrorHandler implementation for times when we don't expect a CALL_ERROR
class NullCallStatusErrorHandler : CallStatusErrorHandler<UniffiException>
{
    public static NullCallStatusErrorHandler INSTANCE = new NullCallStatusErrorHandler();

    public UniffiException Lift(RustBuffer error_buf)
    {
        RustBuffer.Free(error_buf);
        return new UndeclaredErrorException("library has returned an error not declared in UNIFFI interface file");
    }
}

// Helpers for calling Rust
// In practice we usually need to be synchronized to call this safely, so it doesn't
// synchronize itself
class _UniffiHelpers
{
    public delegate void RustCallAction(ref RustCallStatus status);
    public delegate U RustCallFunc<out U>(ref RustCallStatus status);

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static U RustCallWithError<U, E>(CallStatusErrorHandler<E> errorHandler, RustCallFunc<U> callback)
        where E : UniffiException
    {
        var status = new RustCallStatus();
        var return_value = callback(ref status);
        if (status.IsSuccess())
        {
            return return_value;
        }
        else if (status.IsError())
        {
            throw errorHandler.Lift(status.error_buf);
        }
        else if (status.IsPanic())
        {
            // when the rust code sees a panic, it tries to construct a rustbuffer
            // with the message.  but if that code panics, then it just sends back
            // an empty buffer.
            if (status.error_buf.len > 0)
            {
                throw new PanicException(FfiConverterString.INSTANCE.Lift(status.error_buf));
            }
            else
            {
                throw new PanicException("Rust panic");
            }
        }
        else
        {
            throw new InternalException($"Unknown rust call status: {status.code}");
        }
    }

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static void RustCallWithError<E>(CallStatusErrorHandler<E> errorHandler, RustCallAction callback)
        where E : UniffiException
    {
        _UniffiHelpers.RustCallWithError(
            errorHandler,
            (ref RustCallStatus status) =>
            {
                callback(ref status);
                return 0;
            }
        );
    }

    // Call a rust function that returns a plain value
    public static U RustCall<U>(RustCallFunc<U> callback)
    {
        return _UniffiHelpers.RustCallWithError(NullCallStatusErrorHandler.INSTANCE, callback);
    }

    // Call a rust function that returns a plain value
    public static void RustCall(RustCallAction callback)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                callback(ref status);
                return 0;
            }
        );
    }
}

// Big endian streams are not yet available in dotnet :'(
// https://github.com/dotnet/runtime/issues/26904

class StreamUnderflowException : Exception
{
    public StreamUnderflowException() { }
}

class BigEndianStream
{
    Stream stream;

    public BigEndianStream(Stream stream)
    {
        this.stream = stream;
    }

    public bool HasRemaining()
    {
        return (stream.Length - stream.Position) > 0;
    }

    public long Position
    {
        get => stream.Position;
        set => stream.Position = value;
    }

    public void WriteBytes(byte[] value)
    {
        stream.Write(value, 0, value.Length);
    }

    public void WriteByte(byte value)
    {
        stream.WriteByte(value);
    }

    public void WriteUShort(ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteUInt(uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteULong(ulong value)
    {
        WriteUInt((uint)(value >> 32));
        WriteUInt((uint)value);
    }

    public void WriteSByte(sbyte value)
    {
        stream.WriteByte((byte)value);
    }

    public void WriteShort(short value)
    {
        WriteUShort((ushort)value);
    }

    public void WriteInt(int value)
    {
        WriteUInt((uint)value);
    }

    public void WriteFloat(float value)
    {
        WriteInt(BitConverter.SingleToInt32Bits(value));
    }

    public void WriteLong(long value)
    {
        WriteULong((ulong)value);
    }

    public void WriteDouble(double value)
    {
        WriteLong(BitConverter.DoubleToInt64Bits(value));
    }

    public byte[] ReadBytes(int length)
    {
        CheckRemaining(length);
        byte[] result = new byte[length];
        stream.Read(result, 0, length);
        return result;
    }

    public byte ReadByte()
    {
        CheckRemaining(1);
        return Convert.ToByte(stream.ReadByte());
    }

    public ushort ReadUShort()
    {
        CheckRemaining(2);
        return (ushort)(stream.ReadByte() << 8 | stream.ReadByte());
    }

    public uint ReadUInt()
    {
        CheckRemaining(4);
        return (uint)(stream.ReadByte() << 24 | stream.ReadByte() << 16 | stream.ReadByte() << 8 | stream.ReadByte());
    }

    public ulong ReadULong()
    {
        return (ulong)ReadUInt() << 32 | (ulong)ReadUInt();
    }

    public sbyte ReadSByte()
    {
        return (sbyte)ReadByte();
    }

    public short ReadShort()
    {
        return (short)ReadUShort();
    }

    public int ReadInt()
    {
        return (int)ReadUInt();
    }

    public float ReadFloat()
    {
        return BitConverter.Int32BitsToSingle(ReadInt());
    }

    public long ReadLong()
    {
        return (long)ReadULong();
    }

    public double ReadDouble()
    {
        return BitConverter.Int64BitsToDouble(ReadLong());
    }

    private void CheckRemaining(int length)
    {
        if (stream.Length - stream.Position < length)
        {
            throw new StreamUnderflowException();
        }
    }
}

// Contains loading, initialization code,
// and the FFI Function declarations in a com.sun.jna.Library.


// This is an implementation detail which will be called internally by the public API.
static class _UniFFILib
{
    static _UniFFILib()
    {
        FfiConverterTypeClientDelegate.INSTANCE.Register();
        FfiConverterTypeRoomDelegate.INSTANCE.Register();
        FfiConverterTypeSessionVerificationControllerDelegate.INSTANCE.Register();
        FfiConverterTypeSlidingSyncObserver.INSTANCE.Register();
        FfiConverterTypeSlidingSyncViewRoomItemsObserver.INSTANCE.Register();
        FfiConverterTypeSlidingSyncViewRoomListObserver.INSTANCE.Register();
        FfiConverterTypeSlidingSyncViewRoomsCountObserver.INSTANCE.Register();
        FfiConverterTypeSlidingSyncViewStateObserver.INSTANCE.Register();
    }

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_StoppableSpawn_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_StoppableSpawn_is_cancelled(
        StoppableSpawnSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_StoppableSpawn_cancel(
        StoppableSpawnSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncViewBuilder_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_new(
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_timeline_limit(
        SlidingSyncViewBuilderSafeHandle @ptr,
        uint @limit,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_sync_mode(
        SlidingSyncViewBuilderSafeHandle @ptr,
        RustBuffer @mode,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_batch_size(
        SlidingSyncViewBuilderSafeHandle @ptr,
        uint @size,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_name(
        SlidingSyncViewBuilderSafeHandle @ptr,
        RustBuffer @name,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_sort(
        SlidingSyncViewBuilderSafeHandle @ptr,
        RustBuffer @sort,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_add_range(
        SlidingSyncViewBuilderSafeHandle @ptr,
        uint @from,
        uint @to,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_reset_ranges(
        SlidingSyncViewBuilderSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_required_state(
        SlidingSyncViewBuilderSafeHandle @ptr,
        RustBuffer @requiredState,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncViewSafeHandle matrix_sdk_ffi_397b_SlidingSyncViewBuilder_build(
        SlidingSyncViewBuilderSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncView_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern StoppableSpawnSafeHandle matrix_sdk_ffi_397b_SlidingSyncView_observe_room_list(
        SlidingSyncViewSafeHandle @ptr,
        ulong @observer,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern StoppableSpawnSafeHandle matrix_sdk_ffi_397b_SlidingSyncView_observe_rooms_count(
        SlidingSyncViewSafeHandle @ptr,
        ulong @observer,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern StoppableSpawnSafeHandle matrix_sdk_ffi_397b_SlidingSyncView_observe_state(
        SlidingSyncViewSafeHandle @ptr,
        ulong @observer,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern StoppableSpawnSafeHandle matrix_sdk_ffi_397b_SlidingSyncView_observe_room_items(
        SlidingSyncViewSafeHandle @ptr,
        ulong @observer,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncView_current_room_count(
        SlidingSyncViewSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncView_current_rooms_list(
        SlidingSyncViewSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SlidingSyncView_add_range(
        SlidingSyncViewSafeHandle @ptr,
        uint @from,
        uint @to,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SlidingSyncView_reset_ranges(
        SlidingSyncViewSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SlidingSyncView_set_range(
        SlidingSyncViewSafeHandle @ptr,
        uint @from,
        uint @to,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_UnreadNotificationsCount_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_UnreadNotificationsCount_has_notifications(
        UnreadNotificationsCountSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern uint matrix_sdk_ffi_397b_UnreadNotificationsCount_highlight_count(
        UnreadNotificationsCountSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern uint matrix_sdk_ffi_397b_UnreadNotificationsCount_notification_count(
        UnreadNotificationsCountSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncRoom_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncRoom_name(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncRoom_room_id(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncRoom_full_room(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncRoom_is_dm(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncRoom_is_initial(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_SlidingSyncRoom_has_unread_notifications(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern UnreadNotificationsCountSafeHandle matrix_sdk_ffi_397b_SlidingSyncRoom_unread_notifications(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_SlidingSyncRoom_is_loading_more(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSyncRoom_latest_room_message(
        SlidingSyncRoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSync_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SlidingSync_set_observer(
        SlidingSyncSafeHandle @ptr,
        RustBuffer @observer,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern StoppableSpawnSafeHandle matrix_sdk_ffi_397b_SlidingSync_sync(
        SlidingSyncSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SlidingSync_subscribe(
        SlidingSyncSafeHandle @ptr,
        RustBuffer @roomId,
        RustBuffer @settings,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SlidingSync_unsubscribe(
        SlidingSyncSafeHandle @ptr,
        RustBuffer @roomId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSync_get_view(
        SlidingSyncSafeHandle @ptr,
        RustBuffer @name,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSync_get_room(
        SlidingSyncSafeHandle @ptr,
        RustBuffer @roomId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SlidingSync_get_rooms(
        SlidingSyncSafeHandle @ptr,
        RustBuffer @roomIds,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_ClientBuilder_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ClientBuilderSafeHandle matrix_sdk_ffi_397b_ClientBuilder_new(
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ClientBuilderSafeHandle matrix_sdk_ffi_397b_ClientBuilder_base_path(
        ClientBuilderSafeHandle @ptr,
        RustBuffer @path,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ClientBuilderSafeHandle matrix_sdk_ffi_397b_ClientBuilder_username(
        ClientBuilderSafeHandle @ptr,
        RustBuffer @username,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ClientBuilderSafeHandle matrix_sdk_ffi_397b_ClientBuilder_homeserver_url(
        ClientBuilderSafeHandle @ptr,
        RustBuffer @url,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ClientSafeHandle matrix_sdk_ffi_397b_ClientBuilder_build(
        ClientBuilderSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncBuilder_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncBuilder_homeserver(
        SlidingSyncBuilderSafeHandle @ptr,
        RustBuffer @url,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncBuilder_add_fullsync_view(
        SlidingSyncBuilderSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncBuilder_no_views(
        SlidingSyncBuilderSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncBuilderSafeHandle matrix_sdk_ffi_397b_SlidingSyncBuilder_add_view(
        SlidingSyncBuilderSafeHandle @ptr,
        SlidingSyncViewSafeHandle @view,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncSafeHandle matrix_sdk_ffi_397b_SlidingSyncBuilder_build(
        SlidingSyncBuilderSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_Client_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Client_set_delegate(
        ClientSafeHandle @ptr,
        RustBuffer @delegate,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Client_login(
        ClientSafeHandle @ptr,
        RustBuffer @username,
        RustBuffer @password,
        RustBuffer @initialDeviceName,
        RustBuffer @deviceId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Client_restore_login(
        ClientSafeHandle @ptr,
        RustBuffer @restoreToken,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Client_start_sync(
        ClientSafeHandle @ptr,
        RustBuffer @timelineLimit,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_restore_token(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_user_id(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_display_name(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_avatar_url(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_device_id(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_account_data(
        ClientSafeHandle @ptr,
        RustBuffer @eventType,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Client_set_account_data(
        ClientSafeHandle @ptr,
        RustBuffer @eventType,
        RustBuffer @content,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_get_media_content(
        ClientSafeHandle @ptr,
        MediaSourceSafeHandle @source,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Client_get_media_thumbnail(
        ClientSafeHandle @ptr,
        MediaSourceSafeHandle @source,
        ulong @width,
        ulong @height,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SessionVerificationControllerSafeHandle matrix_sdk_ffi_397b_Client_get_session_verification_controller(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncSafeHandle matrix_sdk_ffi_397b_Client_full_sliding_sync(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern SlidingSyncBuilderSafeHandle matrix_sdk_ffi_397b_Client_sliding_sync(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Client_logout(
        ClientSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_Room_object_free(IntPtr @ptr, ref RustCallStatus _uniffi_out_err);

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Room_set_delegate(
        RoomSafeHandle @ptr,
        RustBuffer @delegate,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_id(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_name(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_topic(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_avatar_url(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_membership(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_Room_is_direct(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_Room_is_public(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_Room_is_space(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_Room_is_encrypted(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_Room_is_tombstoned(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_display_name(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_member_avatar_url(
        RoomSafeHandle @ptr,
        RustBuffer @userId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_member_display_name(
        RoomSafeHandle @ptr,
        RustBuffer @userId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_Room_start_live_event_listener(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Room_stop_live_event_listener(
        RoomSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Room_send(
        RoomSafeHandle @ptr,
        RoomMessageEventContentSafeHandle @msg,
        RustBuffer @txnId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Room_send_reply(
        RoomSafeHandle @ptr,
        RustBuffer @msg,
        RustBuffer @inReplyToEventId,
        RustBuffer @txnId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_Room_redact(
        RoomSafeHandle @ptr,
        RustBuffer @eventId,
        RustBuffer @reason,
        RustBuffer @txnId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_BackwardsStream_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_BackwardsStream_paginate_backwards(
        BackwardsStreamSafeHandle @ptr,
        ulong @count,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_RoomMessageEventContent_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_AnyMessage_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_AnyMessage_text_message(
        AnyMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_AnyMessage_image_message(
        AnyMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_AnyMessage_notice_message(
        AnyMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_AnyMessage_emote_message(
        AnyMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_BaseMessage_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_BaseMessage_id(
        BaseMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_BaseMessage_body(
        BaseMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_BaseMessage_sender(
        BaseMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ulong matrix_sdk_ffi_397b_BaseMessage_origin_server_ts(
        BaseMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_BaseMessage_transaction_id(
        BaseMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_TextMessage_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern BaseMessageSafeHandle matrix_sdk_ffi_397b_TextMessage_base_message(
        TextMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_TextMessage_html_body(
        TextMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_ImageMessage_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern BaseMessageSafeHandle matrix_sdk_ffi_397b_ImageMessage_base_message(
        ImageMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern MediaSourceSafeHandle matrix_sdk_ffi_397b_ImageMessage_source(
        ImageMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_ImageMessage_width(
        ImageMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_ImageMessage_height(
        ImageMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_ImageMessage_blurhash(
        ImageMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_NoticeMessage_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern BaseMessageSafeHandle matrix_sdk_ffi_397b_NoticeMessage_base_message(
        NoticeMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_NoticeMessage_html_body(
        NoticeMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_EmoteMessage_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern BaseMessageSafeHandle matrix_sdk_ffi_397b_EmoteMessage_base_message(
        EmoteMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_EmoteMessage_html_body(
        EmoteMessageSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_MediaSource_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_MediaSource_url(
        MediaSourceSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_HomeserverLoginDetails_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_HomeserverLoginDetails_url(
        HomeserverLoginDetailsSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_HomeserverLoginDetails_authentication_issuer(
        HomeserverLoginDetailsSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_HomeserverLoginDetails_supports_password_login(
        HomeserverLoginDetailsSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_AuthenticationService_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern AuthenticationServiceSafeHandle matrix_sdk_ffi_397b_AuthenticationService_new(
        RustBuffer @basePath,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_AuthenticationService_homeserver_details(
        AuthenticationServiceSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_AuthenticationService_configure_homeserver(
        AuthenticationServiceSafeHandle @ptr,
        RustBuffer @serverName,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ClientSafeHandle matrix_sdk_ffi_397b_AuthenticationService_login(
        AuthenticationServiceSafeHandle @ptr,
        RustBuffer @username,
        RustBuffer @password,
        RustBuffer @initialDeviceName,
        RustBuffer @deviceId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern ClientSafeHandle matrix_sdk_ffi_397b_AuthenticationService_restore_with_access_token(
        AuthenticationServiceSafeHandle @ptr,
        RustBuffer @token,
        RustBuffer @deviceId,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SessionVerificationEmoji_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SessionVerificationEmoji_symbol(
        SessionVerificationEmojiSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer matrix_sdk_ffi_397b_SessionVerificationEmoji_description(
        SessionVerificationEmojiSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SessionVerificationController_object_free(
        IntPtr @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SessionVerificationController_set_delegate(
        SessionVerificationControllerSafeHandle @ptr,
        RustBuffer @delegate,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern sbyte matrix_sdk_ffi_397b_SessionVerificationController_is_verified(
        SessionVerificationControllerSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SessionVerificationController_request_verification(
        SessionVerificationControllerSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SessionVerificationController_approve_verification(
        SessionVerificationControllerSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SessionVerificationController_decline_verification(
        SessionVerificationControllerSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void matrix_sdk_ffi_397b_SessionVerificationController_cancel_verification(
        SessionVerificationControllerSafeHandle @ptr,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_ClientDelegate_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncObserver_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncViewStateObserver_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncViewRoomListObserver_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncViewRoomsCountObserver_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SlidingSyncViewRoomItemsObserver_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_RoomDelegate_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_SessionVerificationControllerDelegate_init_callback(
        ForeignCallback @callbackStub,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer ffi_matrix_sdk_ffi_397b_rustbuffer_alloc(
        int @size,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer ffi_matrix_sdk_ffi_397b_rustbuffer_from_bytes(
        ForeignBytes @bytes,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern void ffi_matrix_sdk_ffi_397b_rustbuffer_free(
        RustBuffer @buf,
        ref RustCallStatus _uniffi_out_err
    );

    [DllImport("matrix-sdk")]
    public static extern RustBuffer ffi_matrix_sdk_ffi_397b_rustbuffer_reserve(
        RustBuffer @buf,
        int @additional,
        ref RustCallStatus _uniffi_out_err
    );
}

// Public interface members begin here.

#pragma warning disable 8625




class FfiConverterByte : FfiConverter<byte, byte>
{
    public static FfiConverterByte INSTANCE = new FfiConverterByte();

    public override byte Lift(byte value)
    {
        return value;
    }

    public override byte Read(BigEndianStream stream)
    {
        return stream.ReadByte();
    }

    public override byte Lower(byte value)
    {
        return value;
    }

    public override int AllocationSize(byte value)
    {
        return 1;
    }

    public override void Write(byte value, BigEndianStream stream)
    {
        stream.WriteByte(value);
    }
}

class FfiConverterUShort : FfiConverter<ushort, ushort>
{
    public static FfiConverterUShort INSTANCE = new FfiConverterUShort();

    public override ushort Lift(ushort value)
    {
        return value;
    }

    public override ushort Read(BigEndianStream stream)
    {
        return stream.ReadUShort();
    }

    public override ushort Lower(ushort value)
    {
        return value;
    }

    public override int AllocationSize(ushort value)
    {
        return 2;
    }

    public override void Write(ushort value, BigEndianStream stream)
    {
        stream.WriteUShort(value);
    }
}

class FfiConverterUInt : FfiConverter<uint, uint>
{
    public static FfiConverterUInt INSTANCE = new FfiConverterUInt();

    public override uint Lift(uint value)
    {
        return value;
    }

    public override uint Read(BigEndianStream stream)
    {
        return stream.ReadUInt();
    }

    public override uint Lower(uint value)
    {
        return value;
    }

    public override int AllocationSize(uint value)
    {
        return 4;
    }

    public override void Write(uint value, BigEndianStream stream)
    {
        stream.WriteUInt(value);
    }
}

class FfiConverterULong : FfiConverter<ulong, ulong>
{
    public static FfiConverterULong INSTANCE = new FfiConverterULong();

    public override ulong Lift(ulong value)
    {
        return value;
    }

    public override ulong Read(BigEndianStream stream)
    {
        return stream.ReadULong();
    }

    public override ulong Lower(ulong value)
    {
        return value;
    }

    public override int AllocationSize(ulong value)
    {
        return 8;
    }

    public override void Write(ulong value, BigEndianStream stream)
    {
        stream.WriteULong(value);
    }
}

class FfiConverterBoolean : FfiConverter<bool, sbyte>
{
    public static FfiConverterBoolean INSTANCE = new FfiConverterBoolean();

    public override bool Lift(sbyte value)
    {
        return value != 0;
    }

    public override bool Read(BigEndianStream stream)
    {
        return Lift(stream.ReadSByte());
    }

    public override sbyte Lower(bool value)
    {
        return value ? (sbyte)1 : (sbyte)0;
    }

    public override int AllocationSize(bool value)
    {
        return (sbyte)1;
    }

    public override void Write(bool value, BigEndianStream stream)
    {
        stream.WriteSByte(Lower(value));
    }
}

class FfiConverterString : FfiConverter<string, RustBuffer>
{
    public static FfiConverterString INSTANCE = new FfiConverterString();

    // Note: we don't inherit from FfiConverterRustBuffer, because we use a
    // special encoding when lowering/lifting.  We can use `RustBuffer.len` to
    // store our length and avoid writing it out to the buffer.
    public override string Lift(RustBuffer value)
    {
        try
        {
            var bytes = value.AsStream().ReadBytes(value.len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            RustBuffer.Free(value);
        }
    }

    public override string Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var bytes = stream.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public override RustBuffer Lower(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var rbuf = RustBuffer.Alloc(bytes.Length);
        rbuf.AsWriteableStream().WriteBytes(bytes);
        return rbuf;
    }

    // TODO(CS)
    // We aren't sure exactly how many bytes our string will be once it's UTF-8
    // encoded.  Allocate 3 bytes per unicode codepoint which will always be
    // enough.
    public override int AllocationSize(string value)
    {
        const int sizeForLength = 4;
        var sizeForString = value.Length * 3;
        return sizeForLength + sizeForString;
    }

    public override void Write(string value, BigEndianStream stream)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        stream.WriteInt(bytes.Length);
        stream.WriteBytes(bytes);
    }
}

// `SafeHandle` implements the semantics outlined below, i.e. its thread safe, and the dispose
// method will only be called once, once all outstanding native calls have completed.
// https://github.com/mozilla/uniffi-rs/blob/0dc031132d9493ca812c3af6e7dd60ad2ea95bf0/uniffi_bindgen/src/bindings/kotlin/templates/ObjectRuntime.kt#L31
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.criticalhandle

public abstract class FFIObject<THandle> : IDisposable
    where THandle : FFISafeHandle
{
    private THandle handle;

    public FFIObject(THandle handle)
    {
        this.handle = handle;
    }

    public THandle GetHandle()
    {
        return handle;
    }

    public void Dispose()
    {
        handle.Dispose();
    }
}

public abstract class FFISafeHandle : SafeHandle
{
    public FFISafeHandle()
        : base(new IntPtr(0), true) { }

    public FFISafeHandle(IntPtr pointer)
        : this()
    {
        this.SetHandle(pointer);
    }

    public override bool IsInvalid
    {
        get { return handle.ToInt64() == 0; }
    }

    // TODO(CS) this completely breaks any guarantees offered by SafeHandle.. Extracting
    // raw value from SafeHandle puts responsiblity on the consumer of this function to
    // ensure that SafeHandle outlives the stream, and anyone who might have read the raw
    // value from the stream and are holding onto it. Otherwise, the result might be a use
    // after free, or free while method calls are still in flight.
    //
    // This is also relevant for Kotlin.
    //
    public IntPtr DangerousGetRawFfiValue()
    {
        return handle;
    }
}

static class FFIObjectUtil
{
    public static void DisposeAll(params Object?[] list)
    {
        foreach (var obj in list)
        {
            Dispose(obj);
        }
    }

    // Dispose is implemented by recursive type inspection at runtime. This is because
    // generating correct Dispose calls for recursive complex types, e.g. List<List<int>>
    // is quite cumbersome.
    private static void Dispose(dynamic? obj)
    {
        if (obj == null)
        {
            return;
        }

        if (obj is IDisposable disposable)
        {
            disposable.Dispose();
            return;
        }

        var type = obj.GetType();
        if (type != null)
        {
            if (type.IsGenericType)
            {
                if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(List<>)))
                {
                    foreach (var value in obj)
                    {
                        Dispose(value);
                    }
                }
                else if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(Dictionary<,>)))
                {
                    foreach (var value in obj.Values)
                    {
                        Dispose(value);
                    }
                }
            }
        }
    }
}

public interface IAnyMessage
{
    TextMessage? TextMessage();

    ImageMessage? ImageMessage();

    NoticeMessage? NoticeMessage();

    EmoteMessage? EmoteMessage();
}

public class AnyMessageSafeHandle : FFISafeHandle
{
    public AnyMessageSafeHandle()
        : base() { }

    public AnyMessageSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_AnyMessage_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class AnyMessage : FFIObject<AnyMessageSafeHandle>, IAnyMessage
{
    public AnyMessage(AnyMessageSafeHandle pointer)
        : base(pointer) { }

    public TextMessage? TextMessage()
    {
        return FfiConverterOptionalTypeTextMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AnyMessage_text_message(this.GetHandle(), ref _status)
            )
        );
    }

    public ImageMessage? ImageMessage()
    {
        return FfiConverterOptionalTypeImageMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AnyMessage_image_message(this.GetHandle(), ref _status)
            )
        );
    }

    public NoticeMessage? NoticeMessage()
    {
        return FfiConverterOptionalTypeNoticeMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AnyMessage_notice_message(this.GetHandle(), ref _status)
            )
        );
    }

    public EmoteMessage? EmoteMessage()
    {
        return FfiConverterOptionalTypeEmoteMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AnyMessage_emote_message(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeAnyMessage : FfiConverter<AnyMessage, AnyMessageSafeHandle>
{
    public static FfiConverterTypeAnyMessage INSTANCE = new FfiConverterTypeAnyMessage();

    public override AnyMessageSafeHandle Lower(AnyMessage value)
    {
        return value.GetHandle();
    }

    public override AnyMessage Lift(AnyMessageSafeHandle value)
    {
        return new AnyMessage(value);
    }

    public override AnyMessage Read(BigEndianStream stream)
    {
        return Lift(new AnyMessageSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(AnyMessage value)
    {
        return 8;
    }

    public override void Write(AnyMessage value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IAuthenticationService
{
    HomeserverLoginDetails? HomeserverDetails();

    /// <exception cref="AuthenticationException"></exception>
    void ConfigureHomeserver(String @serverName);

    /// <exception cref="AuthenticationException"></exception>
    Client Login(String @username, String @password, String? @initialDeviceName, String? @deviceId);

    /// <exception cref="AuthenticationException"></exception>
    Client RestoreWithAccessToken(String @token, String @deviceId);
}

public class AuthenticationServiceSafeHandle : FFISafeHandle
{
    public AuthenticationServiceSafeHandle()
        : base() { }

    public AuthenticationServiceSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_AuthenticationService_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class AuthenticationService : FFIObject<AuthenticationServiceSafeHandle>, IAuthenticationService
{
    public AuthenticationService(AuthenticationServiceSafeHandle pointer)
        : base(pointer) { }

    public AuthenticationService(String @basePath)
        : this(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AuthenticationService_new(
                        FfiConverterString.INSTANCE.Lower(@basePath),
                        ref _status
                    )
            )
        ) { }

    public HomeserverLoginDetails? HomeserverDetails()
    {
        return FfiConverterOptionalTypeHomeserverLoginDetails.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AuthenticationService_homeserver_details(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="AuthenticationException"></exception>
    public void ConfigureHomeserver(String @serverName)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeAuthenticationError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_AuthenticationService_configure_homeserver(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@serverName),
                    ref _status
                )
        );
    }

    /// <exception cref="AuthenticationException"></exception>
    public Client Login(String @username, String @password, String? @initialDeviceName, String? @deviceId)
    {
        return FfiConverterTypeClient.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeAuthenticationError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AuthenticationService_login(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@username),
                        FfiConverterString.INSTANCE.Lower(@password),
                        FfiConverterOptionalString.INSTANCE.Lower(@initialDeviceName),
                        FfiConverterOptionalString.INSTANCE.Lower(@deviceId),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="AuthenticationException"></exception>
    public Client RestoreWithAccessToken(String @token, String @deviceId)
    {
        return FfiConverterTypeClient.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeAuthenticationError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_AuthenticationService_restore_with_access_token(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@token),
                        FfiConverterString.INSTANCE.Lower(@deviceId),
                        ref _status
                    )
            )
        );
    }
}

class FfiConverterTypeAuthenticationService : FfiConverter<AuthenticationService, AuthenticationServiceSafeHandle>
{
    public static FfiConverterTypeAuthenticationService INSTANCE = new FfiConverterTypeAuthenticationService();

    public override AuthenticationServiceSafeHandle Lower(AuthenticationService value)
    {
        return value.GetHandle();
    }

    public override AuthenticationService Lift(AuthenticationServiceSafeHandle value)
    {
        return new AuthenticationService(value);
    }

    public override AuthenticationService Read(BigEndianStream stream)
    {
        return Lift(new AuthenticationServiceSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(AuthenticationService value)
    {
        return 8;
    }

    public override void Write(AuthenticationService value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IBackwardsStream
{
    List<AnyMessage> PaginateBackwards(UInt64 @count);
}

public class BackwardsStreamSafeHandle : FFISafeHandle
{
    public BackwardsStreamSafeHandle()
        : base() { }

    public BackwardsStreamSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_BackwardsStream_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class BackwardsStream : FFIObject<BackwardsStreamSafeHandle>, IBackwardsStream
{
    public BackwardsStream(BackwardsStreamSafeHandle pointer)
        : base(pointer) { }

    public List<AnyMessage> PaginateBackwards(UInt64 @count)
    {
        return FfiConverterSequenceTypeAnyMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_BackwardsStream_paginate_backwards(
                        this.GetHandle(),
                        FfiConverterULong.INSTANCE.Lower(@count),
                        ref _status
                    )
            )
        );
    }
}

class FfiConverterTypeBackwardsStream : FfiConverter<BackwardsStream, BackwardsStreamSafeHandle>
{
    public static FfiConverterTypeBackwardsStream INSTANCE = new FfiConverterTypeBackwardsStream();

    public override BackwardsStreamSafeHandle Lower(BackwardsStream value)
    {
        return value.GetHandle();
    }

    public override BackwardsStream Lift(BackwardsStreamSafeHandle value)
    {
        return new BackwardsStream(value);
    }

    public override BackwardsStream Read(BigEndianStream stream)
    {
        return Lift(new BackwardsStreamSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(BackwardsStream value)
    {
        return 8;
    }

    public override void Write(BackwardsStream value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IBaseMessage
{
    String Id();

    String Body();

    String Sender();

    UInt64 OriginServerTs();

    String? TransactionId();
}

public class BaseMessageSafeHandle : FFISafeHandle
{
    public BaseMessageSafeHandle()
        : base() { }

    public BaseMessageSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_BaseMessage_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class BaseMessage : FFIObject<BaseMessageSafeHandle>, IBaseMessage
{
    public BaseMessage(BaseMessageSafeHandle pointer)
        : base(pointer) { }

    public String Id()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_BaseMessage_id(this.GetHandle(), ref _status)
            )
        );
    }

    public String Body()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_BaseMessage_body(this.GetHandle(), ref _status)
            )
        );
    }

    public String Sender()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_BaseMessage_sender(this.GetHandle(), ref _status)
            )
        );
    }

    public UInt64 OriginServerTs()
    {
        return FfiConverterULong.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_BaseMessage_origin_server_ts(this.GetHandle(), ref _status)
            )
        );
    }

    public String? TransactionId()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_BaseMessage_transaction_id(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeBaseMessage : FfiConverter<BaseMessage, BaseMessageSafeHandle>
{
    public static FfiConverterTypeBaseMessage INSTANCE = new FfiConverterTypeBaseMessage();

    public override BaseMessageSafeHandle Lower(BaseMessage value)
    {
        return value.GetHandle();
    }

    public override BaseMessage Lift(BaseMessageSafeHandle value)
    {
        return new BaseMessage(value);
    }

    public override BaseMessage Read(BigEndianStream stream)
    {
        return Lift(new BaseMessageSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(BaseMessage value)
    {
        return 8;
    }

    public override void Write(BaseMessage value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IClient
{
    void SetDelegate(ClientDelegate? @delegate);

    /// <exception cref="ClientException"></exception>
    void Login(String @username, String @password, String? @initialDeviceName, String? @deviceId);

    /// <exception cref="ClientException"></exception>
    void RestoreLogin(String @restoreToken);

    void StartSync(UInt16? @timelineLimit);

    /// <exception cref="ClientException"></exception>
    String RestoreToken();

    /// <exception cref="ClientException"></exception>
    String UserId();

    /// <exception cref="ClientException"></exception>
    String DisplayName();

    /// <exception cref="ClientException"></exception>
    String AvatarUrl();

    /// <exception cref="ClientException"></exception>
    String DeviceId();

    /// <exception cref="ClientException"></exception>
    String? AccountData(String @eventType);

    /// <exception cref="ClientException"></exception>
    void SetAccountData(String @eventType, String @content);

    /// <exception cref="ClientException"></exception>
    List<Byte> GetMediaContent(MediaSource @source);

    /// <exception cref="ClientException"></exception>
    List<Byte> GetMediaThumbnail(MediaSource @source, UInt64 @width, UInt64 @height);

    /// <exception cref="ClientException"></exception>
    SessionVerificationController GetSessionVerificationController();

    /// <exception cref="ClientException"></exception>
    SlidingSync FullSlidingSync();

    SlidingSyncBuilder SlidingSync();

    /// <exception cref="ClientException"></exception>
    void Logout();
}

public class ClientSafeHandle : FFISafeHandle
{
    public ClientSafeHandle()
        : base() { }

    public ClientSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_Client_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class Client : FFIObject<ClientSafeHandle>, IClient
{
    public Client(ClientSafeHandle pointer)
        : base(pointer) { }

    public void SetDelegate(ClientDelegate? @delegate)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Client_set_delegate(
                    this.GetHandle(),
                    FfiConverterOptionalTypeClientDelegate.INSTANCE.Lower(@delegate),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void Login(String @username, String @password, String? @initialDeviceName, String? @deviceId)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Client_login(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@username),
                    FfiConverterString.INSTANCE.Lower(@password),
                    FfiConverterOptionalString.INSTANCE.Lower(@initialDeviceName),
                    FfiConverterOptionalString.INSTANCE.Lower(@deviceId),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void RestoreLogin(String @restoreToken)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Client_restore_login(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@restoreToken),
                    ref _status
                )
        );
    }

    public void StartSync(UInt16? @timelineLimit)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Client_start_sync(
                    this.GetHandle(),
                    FfiConverterOptionalUShort.INSTANCE.Lower(@timelineLimit),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String RestoreToken()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_restore_token(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String UserId()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_user_id(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String DisplayName()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_display_name(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String AvatarUrl()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_avatar_url(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String DeviceId()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_device_id(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String? AccountData(String @eventType)
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_account_data(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@eventType),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void SetAccountData(String @eventType, String @content)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Client_set_account_data(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@eventType),
                    FfiConverterString.INSTANCE.Lower(@content),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public List<Byte> GetMediaContent(MediaSource @source)
    {
        return FfiConverterSequenceByte.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_get_media_content(
                        this.GetHandle(),
                        FfiConverterTypeMediaSource.INSTANCE.Lower(@source),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public List<Byte> GetMediaThumbnail(MediaSource @source, UInt64 @width, UInt64 @height)
    {
        return FfiConverterSequenceByte.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_get_media_thumbnail(
                        this.GetHandle(),
                        FfiConverterTypeMediaSource.INSTANCE.Lower(@source),
                        FfiConverterULong.INSTANCE.Lower(@width),
                        FfiConverterULong.INSTANCE.Lower(@height),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public SessionVerificationController GetSessionVerificationController()
    {
        return FfiConverterTypeSessionVerificationController.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_get_session_verification_controller(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public SlidingSync FullSlidingSync()
    {
        return FfiConverterTypeSlidingSync.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_full_sliding_sync(this.GetHandle(), ref _status)
            )
        );
    }

    public SlidingSyncBuilder SlidingSync()
    {
        return FfiConverterTypeSlidingSyncBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Client_sliding_sync(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void Logout()
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) => _UniFFILib.matrix_sdk_ffi_397b_Client_logout(this.GetHandle(), ref _status)
        );
    }
}

class FfiConverterTypeClient : FfiConverter<Client, ClientSafeHandle>
{
    public static FfiConverterTypeClient INSTANCE = new FfiConverterTypeClient();

    public override ClientSafeHandle Lower(Client value)
    {
        return value.GetHandle();
    }

    public override Client Lift(ClientSafeHandle value)
    {
        return new Client(value);
    }

    public override Client Read(BigEndianStream stream)
    {
        return Lift(new ClientSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(Client value)
    {
        return 8;
    }

    public override void Write(Client value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IClientBuilder
{
    ClientBuilder BasePath(String @path);

    ClientBuilder Username(String @username);

    ClientBuilder HomeserverUrl(String @url);

    /// <exception cref="ClientException"></exception>
    Client Build();
}

public class ClientBuilderSafeHandle : FFISafeHandle
{
    public ClientBuilderSafeHandle()
        : base() { }

    public ClientBuilderSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_ClientBuilder_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class ClientBuilder : FFIObject<ClientBuilderSafeHandle>, IClientBuilder
{
    public ClientBuilder(ClientBuilderSafeHandle pointer)
        : base(pointer) { }

    public ClientBuilder()
        : this(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) => _UniFFILib.matrix_sdk_ffi_397b_ClientBuilder_new(ref _status)
            )
        ) { }

    public ClientBuilder BasePath(String @path)
    {
        return FfiConverterTypeClientBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ClientBuilder_base_path(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@path),
                        ref _status
                    )
            )
        );
    }

    public ClientBuilder Username(String @username)
    {
        return FfiConverterTypeClientBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ClientBuilder_username(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@username),
                        ref _status
                    )
            )
        );
    }

    public ClientBuilder HomeserverUrl(String @url)
    {
        return FfiConverterTypeClientBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ClientBuilder_homeserver_url(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@url),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public Client Build()
    {
        return FfiConverterTypeClient.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ClientBuilder_build(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeClientBuilder : FfiConverter<ClientBuilder, ClientBuilderSafeHandle>
{
    public static FfiConverterTypeClientBuilder INSTANCE = new FfiConverterTypeClientBuilder();

    public override ClientBuilderSafeHandle Lower(ClientBuilder value)
    {
        return value.GetHandle();
    }

    public override ClientBuilder Lift(ClientBuilderSafeHandle value)
    {
        return new ClientBuilder(value);
    }

    public override ClientBuilder Read(BigEndianStream stream)
    {
        return Lift(new ClientBuilderSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(ClientBuilder value)
    {
        return 8;
    }

    public override void Write(ClientBuilder value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IEmoteMessage
{
    BaseMessage BaseMessage();

    String? HtmlBody();
}

public class EmoteMessageSafeHandle : FFISafeHandle
{
    public EmoteMessageSafeHandle()
        : base() { }

    public EmoteMessageSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_EmoteMessage_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class EmoteMessage : FFIObject<EmoteMessageSafeHandle>, IEmoteMessage
{
    public EmoteMessage(EmoteMessageSafeHandle pointer)
        : base(pointer) { }

    public BaseMessage BaseMessage()
    {
        return FfiConverterTypeBaseMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_EmoteMessage_base_message(this.GetHandle(), ref _status)
            )
        );
    }

    public String? HtmlBody()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_EmoteMessage_html_body(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeEmoteMessage : FfiConverter<EmoteMessage, EmoteMessageSafeHandle>
{
    public static FfiConverterTypeEmoteMessage INSTANCE = new FfiConverterTypeEmoteMessage();

    public override EmoteMessageSafeHandle Lower(EmoteMessage value)
    {
        return value.GetHandle();
    }

    public override EmoteMessage Lift(EmoteMessageSafeHandle value)
    {
        return new EmoteMessage(value);
    }

    public override EmoteMessage Read(BigEndianStream stream)
    {
        return Lift(new EmoteMessageSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(EmoteMessage value)
    {
        return 8;
    }

    public override void Write(EmoteMessage value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IHomeserverLoginDetails
{
    String Url();

    String? AuthenticationIssuer();

    Boolean SupportsPasswordLogin();
}

public class HomeserverLoginDetailsSafeHandle : FFISafeHandle
{
    public HomeserverLoginDetailsSafeHandle()
        : base() { }

    public HomeserverLoginDetailsSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_HomeserverLoginDetails_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class HomeserverLoginDetails : FFIObject<HomeserverLoginDetailsSafeHandle>, IHomeserverLoginDetails
{
    public HomeserverLoginDetails(HomeserverLoginDetailsSafeHandle pointer)
        : base(pointer) { }

    public String Url()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_HomeserverLoginDetails_url(this.GetHandle(), ref _status)
            )
        );
    }

    public String? AuthenticationIssuer()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_HomeserverLoginDetails_authentication_issuer(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }

    public Boolean SupportsPasswordLogin()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_HomeserverLoginDetails_supports_password_login(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }
}

class FfiConverterTypeHomeserverLoginDetails : FfiConverter<HomeserverLoginDetails, HomeserverLoginDetailsSafeHandle>
{
    public static FfiConverterTypeHomeserverLoginDetails INSTANCE = new FfiConverterTypeHomeserverLoginDetails();

    public override HomeserverLoginDetailsSafeHandle Lower(HomeserverLoginDetails value)
    {
        return value.GetHandle();
    }

    public override HomeserverLoginDetails Lift(HomeserverLoginDetailsSafeHandle value)
    {
        return new HomeserverLoginDetails(value);
    }

    public override HomeserverLoginDetails Read(BigEndianStream stream)
    {
        return Lift(new HomeserverLoginDetailsSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(HomeserverLoginDetails value)
    {
        return 8;
    }

    public override void Write(HomeserverLoginDetails value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IImageMessage
{
    BaseMessage BaseMessage();

    MediaSource Source();

    UInt64? Width();

    UInt64? Height();

    String? Blurhash();
}

public class ImageMessageSafeHandle : FFISafeHandle
{
    public ImageMessageSafeHandle()
        : base() { }

    public ImageMessageSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_ImageMessage_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class ImageMessage : FFIObject<ImageMessageSafeHandle>, IImageMessage
{
    public ImageMessage(ImageMessageSafeHandle pointer)
        : base(pointer) { }

    public BaseMessage BaseMessage()
    {
        return FfiConverterTypeBaseMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ImageMessage_base_message(this.GetHandle(), ref _status)
            )
        );
    }

    public MediaSource Source()
    {
        return FfiConverterTypeMediaSource.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ImageMessage_source(this.GetHandle(), ref _status)
            )
        );
    }

    public UInt64? Width()
    {
        return FfiConverterOptionalULong.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ImageMessage_width(this.GetHandle(), ref _status)
            )
        );
    }

    public UInt64? Height()
    {
        return FfiConverterOptionalULong.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ImageMessage_height(this.GetHandle(), ref _status)
            )
        );
    }

    public String? Blurhash()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_ImageMessage_blurhash(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeImageMessage : FfiConverter<ImageMessage, ImageMessageSafeHandle>
{
    public static FfiConverterTypeImageMessage INSTANCE = new FfiConverterTypeImageMessage();

    public override ImageMessageSafeHandle Lower(ImageMessage value)
    {
        return value.GetHandle();
    }

    public override ImageMessage Lift(ImageMessageSafeHandle value)
    {
        return new ImageMessage(value);
    }

    public override ImageMessage Read(BigEndianStream stream)
    {
        return Lift(new ImageMessageSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(ImageMessage value)
    {
        return 8;
    }

    public override void Write(ImageMessage value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IMediaSource
{
    String Url();
}

public class MediaSourceSafeHandle : FFISafeHandle
{
    public MediaSourceSafeHandle()
        : base() { }

    public MediaSourceSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_MediaSource_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class MediaSource : FFIObject<MediaSourceSafeHandle>, IMediaSource
{
    public MediaSource(MediaSourceSafeHandle pointer)
        : base(pointer) { }

    public String Url()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_MediaSource_url(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeMediaSource : FfiConverter<MediaSource, MediaSourceSafeHandle>
{
    public static FfiConverterTypeMediaSource INSTANCE = new FfiConverterTypeMediaSource();

    public override MediaSourceSafeHandle Lower(MediaSource value)
    {
        return value.GetHandle();
    }

    public override MediaSource Lift(MediaSourceSafeHandle value)
    {
        return new MediaSource(value);
    }

    public override MediaSource Read(BigEndianStream stream)
    {
        return Lift(new MediaSourceSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(MediaSource value)
    {
        return 8;
    }

    public override void Write(MediaSource value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface INoticeMessage
{
    BaseMessage BaseMessage();

    String? HtmlBody();
}

public class NoticeMessageSafeHandle : FFISafeHandle
{
    public NoticeMessageSafeHandle()
        : base() { }

    public NoticeMessageSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_NoticeMessage_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class NoticeMessage : FFIObject<NoticeMessageSafeHandle>, INoticeMessage
{
    public NoticeMessage(NoticeMessageSafeHandle pointer)
        : base(pointer) { }

    public BaseMessage BaseMessage()
    {
        return FfiConverterTypeBaseMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_NoticeMessage_base_message(this.GetHandle(), ref _status)
            )
        );
    }

    public String? HtmlBody()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_NoticeMessage_html_body(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeNoticeMessage : FfiConverter<NoticeMessage, NoticeMessageSafeHandle>
{
    public static FfiConverterTypeNoticeMessage INSTANCE = new FfiConverterTypeNoticeMessage();

    public override NoticeMessageSafeHandle Lower(NoticeMessage value)
    {
        return value.GetHandle();
    }

    public override NoticeMessage Lift(NoticeMessageSafeHandle value)
    {
        return new NoticeMessage(value);
    }

    public override NoticeMessage Read(BigEndianStream stream)
    {
        return Lift(new NoticeMessageSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(NoticeMessage value)
    {
        return 8;
    }

    public override void Write(NoticeMessage value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IRoom
{
    void SetDelegate(RoomDelegate? @delegate);

    String Id();

    String? Name();

    String? Topic();

    String? AvatarUrl();

    Membership Membership();

    Boolean IsDirect();

    Boolean IsPublic();

    Boolean IsSpace();

    Boolean IsEncrypted();

    Boolean IsTombstoned();

    /// <exception cref="ClientException"></exception>
    String DisplayName();

    /// <exception cref="ClientException"></exception>
    String? MemberAvatarUrl(String @userId);

    /// <exception cref="ClientException"></exception>
    String? MemberDisplayName(String @userId);

    BackwardsStream? StartLiveEventListener();

    void StopLiveEventListener();

    /// <exception cref="ClientException"></exception>
    void Send(RoomMessageEventContent @msg, String? @txnId);

    /// <exception cref="ClientException"></exception>
    void SendReply(String @msg, String @inReplyToEventId, String? @txnId);

    /// <exception cref="ClientException"></exception>
    void Redact(String @eventId, String? @reason, String? @txnId);
}

public class RoomSafeHandle : FFISafeHandle
{
    public RoomSafeHandle()
        : base() { }

    public RoomSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_Room_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class Room : FFIObject<RoomSafeHandle>, IRoom
{
    public Room(RoomSafeHandle pointer)
        : base(pointer) { }

    public void SetDelegate(RoomDelegate? @delegate)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Room_set_delegate(
                    this.GetHandle(),
                    FfiConverterOptionalTypeRoomDelegate.INSTANCE.Lower(@delegate),
                    ref _status
                )
        );
    }

    public String Id()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) => _UniFFILib.matrix_sdk_ffi_397b_Room_id(this.GetHandle(), ref _status)
            )
        );
    }

    public String? Name()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) => _UniFFILib.matrix_sdk_ffi_397b_Room_name(this.GetHandle(), ref _status)
            )
        );
    }

    public String? Topic()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) => _UniFFILib.matrix_sdk_ffi_397b_Room_topic(this.GetHandle(), ref _status)
            )
        );
    }

    public String? AvatarUrl()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_avatar_url(this.GetHandle(), ref _status)
            )
        );
    }

    public Membership Membership()
    {
        return FfiConverterTypeMembership.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_membership(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean IsDirect()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_is_direct(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean IsPublic()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_is_public(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean IsSpace()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_is_space(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean IsEncrypted()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_is_encrypted(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean IsTombstoned()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_is_tombstoned(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String DisplayName()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_display_name(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String? MemberAvatarUrl(String @userId)
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_member_avatar_url(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@userId),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public String? MemberDisplayName(String @userId)
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_member_display_name(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@userId),
                        ref _status
                    )
            )
        );
    }

    public BackwardsStream? StartLiveEventListener()
    {
        return FfiConverterOptionalTypeBackwardsStream.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_Room_start_live_event_listener(this.GetHandle(), ref _status)
            )
        );
    }

    public void StopLiveEventListener()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Room_stop_live_event_listener(this.GetHandle(), ref _status)
        );
    }

    /// <exception cref="ClientException"></exception>
    public void Send(RoomMessageEventContent @msg, String? @txnId)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Room_send(
                    this.GetHandle(),
                    FfiConverterTypeRoomMessageEventContent.INSTANCE.Lower(@msg),
                    FfiConverterOptionalString.INSTANCE.Lower(@txnId),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void SendReply(String @msg, String @inReplyToEventId, String? @txnId)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Room_send_reply(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@msg),
                    FfiConverterString.INSTANCE.Lower(@inReplyToEventId),
                    FfiConverterOptionalString.INSTANCE.Lower(@txnId),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void Redact(String @eventId, String? @reason, String? @txnId)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_Room_redact(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@eventId),
                    FfiConverterOptionalString.INSTANCE.Lower(@reason),
                    FfiConverterOptionalString.INSTANCE.Lower(@txnId),
                    ref _status
                )
        );
    }
}

class FfiConverterTypeRoom : FfiConverter<Room, RoomSafeHandle>
{
    public static FfiConverterTypeRoom INSTANCE = new FfiConverterTypeRoom();

    public override RoomSafeHandle Lower(Room value)
    {
        return value.GetHandle();
    }

    public override Room Lift(RoomSafeHandle value)
    {
        return new Room(value);
    }

    public override Room Read(BigEndianStream stream)
    {
        return Lift(new RoomSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(Room value)
    {
        return 8;
    }

    public override void Write(Room value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IRoomMessageEventContent { }

public class RoomMessageEventContentSafeHandle : FFISafeHandle
{
    public RoomMessageEventContentSafeHandle()
        : base() { }

    public RoomMessageEventContentSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_RoomMessageEventContent_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class RoomMessageEventContent : FFIObject<RoomMessageEventContentSafeHandle>, IRoomMessageEventContent
{
    public RoomMessageEventContent(RoomMessageEventContentSafeHandle pointer)
        : base(pointer) { }
}

class FfiConverterTypeRoomMessageEventContent : FfiConverter<RoomMessageEventContent, RoomMessageEventContentSafeHandle>
{
    public static FfiConverterTypeRoomMessageEventContent INSTANCE = new FfiConverterTypeRoomMessageEventContent();

    public override RoomMessageEventContentSafeHandle Lower(RoomMessageEventContent value)
    {
        return value.GetHandle();
    }

    public override RoomMessageEventContent Lift(RoomMessageEventContentSafeHandle value)
    {
        return new RoomMessageEventContent(value);
    }

    public override RoomMessageEventContent Read(BigEndianStream stream)
    {
        return Lift(new RoomMessageEventContentSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(RoomMessageEventContent value)
    {
        return 8;
    }

    public override void Write(RoomMessageEventContent value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ISessionVerificationController
{
    void SetDelegate(SessionVerificationControllerDelegate? @delegate);

    Boolean IsVerified();

    /// <exception cref="ClientException"></exception>
    void RequestVerification();

    /// <exception cref="ClientException"></exception>
    void ApproveVerification();

    /// <exception cref="ClientException"></exception>
    void DeclineVerification();

    /// <exception cref="ClientException"></exception>
    void CancelVerification();
}

public class SessionVerificationControllerSafeHandle : FFISafeHandle
{
    public SessionVerificationControllerSafeHandle()
        : base() { }

    public SessionVerificationControllerSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SessionVerificationController_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class SessionVerificationController
    : FFIObject<SessionVerificationControllerSafeHandle>,
        ISessionVerificationController
{
    public SessionVerificationController(SessionVerificationControllerSafeHandle pointer)
        : base(pointer) { }

    public void SetDelegate(SessionVerificationControllerDelegate? @delegate)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationController_set_delegate(
                    this.GetHandle(),
                    FfiConverterOptionalTypeSessionVerificationControllerDelegate.INSTANCE.Lower(@delegate),
                    ref _status
                )
        );
    }

    public Boolean IsVerified()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationController_is_verified(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void RequestVerification()
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationController_request_verification(
                    this.GetHandle(),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void ApproveVerification()
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationController_approve_verification(
                    this.GetHandle(),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void DeclineVerification()
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationController_decline_verification(
                    this.GetHandle(),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void CancelVerification()
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationController_cancel_verification(
                    this.GetHandle(),
                    ref _status
                )
        );
    }
}

class FfiConverterTypeSessionVerificationController
    : FfiConverter<SessionVerificationController, SessionVerificationControllerSafeHandle>
{
    public static FfiConverterTypeSessionVerificationController INSTANCE =
        new FfiConverterTypeSessionVerificationController();

    public override SessionVerificationControllerSafeHandle Lower(SessionVerificationController value)
    {
        return value.GetHandle();
    }

    public override SessionVerificationController Lift(SessionVerificationControllerSafeHandle value)
    {
        return new SessionVerificationController(value);
    }

    public override SessionVerificationController Read(BigEndianStream stream)
    {
        return Lift(new SessionVerificationControllerSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(SessionVerificationController value)
    {
        return 8;
    }

    public override void Write(SessionVerificationController value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ISessionVerificationEmoji
{
    String Symbol();

    String Description();
}

public class SessionVerificationEmojiSafeHandle : FFISafeHandle
{
    public SessionVerificationEmojiSafeHandle()
        : base() { }

    public SessionVerificationEmojiSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SessionVerificationEmoji_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class SessionVerificationEmoji : FFIObject<SessionVerificationEmojiSafeHandle>, ISessionVerificationEmoji
{
    public SessionVerificationEmoji(SessionVerificationEmojiSafeHandle pointer)
        : base(pointer) { }

    public String Symbol()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationEmoji_symbol(this.GetHandle(), ref _status)
            )
        );
    }

    public String Description()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SessionVerificationEmoji_description(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeSessionVerificationEmoji
    : FfiConverter<SessionVerificationEmoji, SessionVerificationEmojiSafeHandle>
{
    public static FfiConverterTypeSessionVerificationEmoji INSTANCE = new FfiConverterTypeSessionVerificationEmoji();

    public override SessionVerificationEmojiSafeHandle Lower(SessionVerificationEmoji value)
    {
        return value.GetHandle();
    }

    public override SessionVerificationEmoji Lift(SessionVerificationEmojiSafeHandle value)
    {
        return new SessionVerificationEmoji(value);
    }

    public override SessionVerificationEmoji Read(BigEndianStream stream)
    {
        return Lift(new SessionVerificationEmojiSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(SessionVerificationEmoji value)
    {
        return 8;
    }

    public override void Write(SessionVerificationEmoji value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ISlidingSync
{
    void SetObserver(SlidingSyncObserver? @observer);

    StoppableSpawn Sync();

    /// <exception cref="ClientException"></exception>
    void Subscribe(String @roomId, RoomSubscription? @settings);

    /// <exception cref="ClientException"></exception>
    void Unsubscribe(String @roomId);

    SlidingSyncView? GetView(String @name);

    /// <exception cref="ClientException"></exception>
    SlidingSyncRoom? GetRoom(String @roomId);

    /// <exception cref="ClientException"></exception>
    List<SlidingSyncRoom?> GetRooms(List<String> @roomIds);
}

public class SlidingSyncSafeHandle : FFISafeHandle
{
    public SlidingSyncSafeHandle()
        : base() { }

    public SlidingSyncSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSync_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class SlidingSync : FFIObject<SlidingSyncSafeHandle>, ISlidingSync
{
    public SlidingSync(SlidingSyncSafeHandle pointer)
        : base(pointer) { }

    public void SetObserver(SlidingSyncObserver? @observer)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SlidingSync_set_observer(
                    this.GetHandle(),
                    FfiConverterOptionalTypeSlidingSyncObserver.INSTANCE.Lower(@observer),
                    ref _status
                )
        );
    }

    public StoppableSpawn Sync()
    {
        return FfiConverterTypeStoppableSpawn.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSync_sync(this.GetHandle(), ref _status)
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void Subscribe(String @roomId, RoomSubscription? @settings)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SlidingSync_subscribe(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@roomId),
                    FfiConverterOptionalTypeRoomSubscription.INSTANCE.Lower(@settings),
                    ref _status
                )
        );
    }

    /// <exception cref="ClientException"></exception>
    public void Unsubscribe(String @roomId)
    {
        _UniffiHelpers.RustCallWithError(
            FfiConverterTypeClientError.INSTANCE,
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SlidingSync_unsubscribe(
                    this.GetHandle(),
                    FfiConverterString.INSTANCE.Lower(@roomId),
                    ref _status
                )
        );
    }

    public SlidingSyncView? GetView(String @name)
    {
        return FfiConverterOptionalTypeSlidingSyncView.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSync_get_view(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@name),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public SlidingSyncRoom? GetRoom(String @roomId)
    {
        return FfiConverterOptionalTypeSlidingSyncRoom.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSync_get_room(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@roomId),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public List<SlidingSyncRoom?> GetRooms(List<String> @roomIds)
    {
        return FfiConverterSequenceOptionalTypeSlidingSyncRoom.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSync_get_rooms(
                        this.GetHandle(),
                        FfiConverterSequenceString.INSTANCE.Lower(@roomIds),
                        ref _status
                    )
            )
        );
    }
}

class FfiConverterTypeSlidingSync : FfiConverter<SlidingSync, SlidingSyncSafeHandle>
{
    public static FfiConverterTypeSlidingSync INSTANCE = new FfiConverterTypeSlidingSync();

    public override SlidingSyncSafeHandle Lower(SlidingSync value)
    {
        return value.GetHandle();
    }

    public override SlidingSync Lift(SlidingSyncSafeHandle value)
    {
        return new SlidingSync(value);
    }

    public override SlidingSync Read(BigEndianStream stream)
    {
        return Lift(new SlidingSyncSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(SlidingSync value)
    {
        return 8;
    }

    public override void Write(SlidingSync value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ISlidingSyncBuilder
{
    /// <exception cref="ClientException"></exception>
    SlidingSyncBuilder Homeserver(String @url);

    SlidingSyncBuilder AddFullsyncView();

    SlidingSyncBuilder NoViews();

    SlidingSyncBuilder AddView(SlidingSyncView @view);

    /// <exception cref="ClientException"></exception>
    SlidingSync Build();
}

public class SlidingSyncBuilderSafeHandle : FFISafeHandle
{
    public SlidingSyncBuilderSafeHandle()
        : base() { }

    public SlidingSyncBuilderSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncBuilder_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class SlidingSyncBuilder : FFIObject<SlidingSyncBuilderSafeHandle>, ISlidingSyncBuilder
{
    public SlidingSyncBuilder(SlidingSyncBuilderSafeHandle pointer)
        : base(pointer) { }

    /// <exception cref="ClientException"></exception>
    public SlidingSyncBuilder Homeserver(String @url)
    {
        return FfiConverterTypeSlidingSyncBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncBuilder_homeserver(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@url),
                        ref _status
                    )
            )
        );
    }

    public SlidingSyncBuilder AddFullsyncView()
    {
        return FfiConverterTypeSlidingSyncBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncBuilder_add_fullsync_view(this.GetHandle(), ref _status)
            )
        );
    }

    public SlidingSyncBuilder NoViews()
    {
        return FfiConverterTypeSlidingSyncBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncBuilder_no_views(this.GetHandle(), ref _status)
            )
        );
    }

    public SlidingSyncBuilder AddView(SlidingSyncView @view)
    {
        return FfiConverterTypeSlidingSyncBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncBuilder_add_view(
                        this.GetHandle(),
                        FfiConverterTypeSlidingSyncView.INSTANCE.Lower(@view),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public SlidingSync Build()
    {
        return FfiConverterTypeSlidingSync.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncBuilder_build(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeSlidingSyncBuilder : FfiConverter<SlidingSyncBuilder, SlidingSyncBuilderSafeHandle>
{
    public static FfiConverterTypeSlidingSyncBuilder INSTANCE = new FfiConverterTypeSlidingSyncBuilder();

    public override SlidingSyncBuilderSafeHandle Lower(SlidingSyncBuilder value)
    {
        return value.GetHandle();
    }

    public override SlidingSyncBuilder Lift(SlidingSyncBuilderSafeHandle value)
    {
        return new SlidingSyncBuilder(value);
    }

    public override SlidingSyncBuilder Read(BigEndianStream stream)
    {
        return Lift(new SlidingSyncBuilderSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(SlidingSyncBuilder value)
    {
        return 8;
    }

    public override void Write(SlidingSyncBuilder value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ISlidingSyncRoom
{
    String? Name();

    String RoomId();

    Room? FullRoom();

    Boolean? IsDm();

    Boolean? IsInitial();

    Boolean HasUnreadNotifications();

    UnreadNotificationsCount UnreadNotifications();

    Boolean IsLoadingMore();

    AnyMessage? LatestRoomMessage();
}

public class SlidingSyncRoomSafeHandle : FFISafeHandle
{
    public SlidingSyncRoomSafeHandle()
        : base() { }

    public SlidingSyncRoomSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncRoom_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class SlidingSyncRoom : FFIObject<SlidingSyncRoomSafeHandle>, ISlidingSyncRoom
{
    public SlidingSyncRoom(SlidingSyncRoomSafeHandle pointer)
        : base(pointer) { }

    public String? Name()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_name(this.GetHandle(), ref _status)
            )
        );
    }

    public String RoomId()
    {
        return FfiConverterString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_room_id(this.GetHandle(), ref _status)
            )
        );
    }

    public Room? FullRoom()
    {
        return FfiConverterOptionalTypeRoom.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_full_room(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean? IsDm()
    {
        return FfiConverterOptionalBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_is_dm(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean? IsInitial()
    {
        return FfiConverterOptionalBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_is_initial(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean HasUnreadNotifications()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_has_unread_notifications(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }

    public UnreadNotificationsCount UnreadNotifications()
    {
        return FfiConverterTypeUnreadNotificationsCount.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_unread_notifications(this.GetHandle(), ref _status)
            )
        );
    }

    public Boolean IsLoadingMore()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_is_loading_more(this.GetHandle(), ref _status)
            )
        );
    }

    public AnyMessage? LatestRoomMessage()
    {
        return FfiConverterOptionalTypeAnyMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncRoom_latest_room_message(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeSlidingSyncRoom : FfiConverter<SlidingSyncRoom, SlidingSyncRoomSafeHandle>
{
    public static FfiConverterTypeSlidingSyncRoom INSTANCE = new FfiConverterTypeSlidingSyncRoom();

    public override SlidingSyncRoomSafeHandle Lower(SlidingSyncRoom value)
    {
        return value.GetHandle();
    }

    public override SlidingSyncRoom Lift(SlidingSyncRoomSafeHandle value)
    {
        return new SlidingSyncRoom(value);
    }

    public override SlidingSyncRoom Read(BigEndianStream stream)
    {
        return Lift(new SlidingSyncRoomSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(SlidingSyncRoom value)
    {
        return 8;
    }

    public override void Write(SlidingSyncRoom value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ISlidingSyncView
{
    StoppableSpawn ObserveRoomList(SlidingSyncViewRoomListObserver @observer);

    StoppableSpawn ObserveRoomsCount(SlidingSyncViewRoomsCountObserver @observer);

    StoppableSpawn ObserveState(SlidingSyncViewStateObserver @observer);

    StoppableSpawn ObserveRoomItems(SlidingSyncViewRoomItemsObserver @observer);

    UInt32? CurrentRoomCount();

    List<RoomListEntry> CurrentRoomsList();

    void AddRange(UInt32 @from, UInt32 @to);

    void ResetRanges();

    void SetRange(UInt32 @from, UInt32 @to);
}

public class SlidingSyncViewSafeHandle : FFISafeHandle
{
    public SlidingSyncViewSafeHandle()
        : base() { }

    public SlidingSyncViewSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncView_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class SlidingSyncView : FFIObject<SlidingSyncViewSafeHandle>, ISlidingSyncView
{
    public SlidingSyncView(SlidingSyncViewSafeHandle pointer)
        : base(pointer) { }

    public StoppableSpawn ObserveRoomList(SlidingSyncViewRoomListObserver @observer)
    {
        return FfiConverterTypeStoppableSpawn.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_observe_room_list(
                        this.GetHandle(),
                        FfiConverterTypeSlidingSyncViewRoomListObserver.INSTANCE.Lower(@observer),
                        ref _status
                    )
            )
        );
    }

    public StoppableSpawn ObserveRoomsCount(SlidingSyncViewRoomsCountObserver @observer)
    {
        return FfiConverterTypeStoppableSpawn.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_observe_rooms_count(
                        this.GetHandle(),
                        FfiConverterTypeSlidingSyncViewRoomsCountObserver.INSTANCE.Lower(@observer),
                        ref _status
                    )
            )
        );
    }

    public StoppableSpawn ObserveState(SlidingSyncViewStateObserver @observer)
    {
        return FfiConverterTypeStoppableSpawn.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_observe_state(
                        this.GetHandle(),
                        FfiConverterTypeSlidingSyncViewStateObserver.INSTANCE.Lower(@observer),
                        ref _status
                    )
            )
        );
    }

    public StoppableSpawn ObserveRoomItems(SlidingSyncViewRoomItemsObserver @observer)
    {
        return FfiConverterTypeStoppableSpawn.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_observe_room_items(
                        this.GetHandle(),
                        FfiConverterTypeSlidingSyncViewRoomItemsObserver.INSTANCE.Lower(@observer),
                        ref _status
                    )
            )
        );
    }

    public UInt32? CurrentRoomCount()
    {
        return FfiConverterOptionalUInt.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_current_room_count(this.GetHandle(), ref _status)
            )
        );
    }

    public List<RoomListEntry> CurrentRoomsList()
    {
        return FfiConverterSequenceTypeRoomListEntry.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_current_rooms_list(this.GetHandle(), ref _status)
            )
        );
    }

    public void AddRange(UInt32 @from, UInt32 @to)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_add_range(
                    this.GetHandle(),
                    FfiConverterUInt.INSTANCE.Lower(@from),
                    FfiConverterUInt.INSTANCE.Lower(@to),
                    ref _status
                )
        );
    }

    public void ResetRanges()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_reset_ranges(this.GetHandle(), ref _status)
        );
    }

    public void SetRange(UInt32 @from, UInt32 @to)
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncView_set_range(
                    this.GetHandle(),
                    FfiConverterUInt.INSTANCE.Lower(@from),
                    FfiConverterUInt.INSTANCE.Lower(@to),
                    ref _status
                )
        );
    }
}

class FfiConverterTypeSlidingSyncView : FfiConverter<SlidingSyncView, SlidingSyncViewSafeHandle>
{
    public static FfiConverterTypeSlidingSyncView INSTANCE = new FfiConverterTypeSlidingSyncView();

    public override SlidingSyncViewSafeHandle Lower(SlidingSyncView value)
    {
        return value.GetHandle();
    }

    public override SlidingSyncView Lift(SlidingSyncViewSafeHandle value)
    {
        return new SlidingSyncView(value);
    }

    public override SlidingSyncView Read(BigEndianStream stream)
    {
        return Lift(new SlidingSyncViewSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(SlidingSyncView value)
    {
        return 8;
    }

    public override void Write(SlidingSyncView value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ISlidingSyncViewBuilder
{
    SlidingSyncViewBuilder TimelineLimit(UInt32 @limit);

    SlidingSyncViewBuilder SyncMode(SlidingSyncMode @mode);

    SlidingSyncViewBuilder BatchSize(UInt32 @size);

    SlidingSyncViewBuilder Name(String @name);

    SlidingSyncViewBuilder Sort(List<String> @sort);

    SlidingSyncViewBuilder AddRange(UInt32 @from, UInt32 @to);

    SlidingSyncViewBuilder ResetRanges();

    SlidingSyncViewBuilder RequiredState(List<RequiredState> @requiredState);

    /// <exception cref="ClientException"></exception>
    SlidingSyncView Build();
}

public class SlidingSyncViewBuilderSafeHandle : FFISafeHandle
{
    public SlidingSyncViewBuilderSafeHandle()
        : base() { }

    public SlidingSyncViewBuilderSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncViewBuilder_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class SlidingSyncViewBuilder : FFIObject<SlidingSyncViewBuilderSafeHandle>, ISlidingSyncViewBuilder
{
    public SlidingSyncViewBuilder(SlidingSyncViewBuilderSafeHandle pointer)
        : base(pointer) { }

    public SlidingSyncViewBuilder()
        : this(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) => _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_new(ref _status)
            )
        ) { }

    public SlidingSyncViewBuilder TimelineLimit(UInt32 @limit)
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_timeline_limit(
                        this.GetHandle(),
                        FfiConverterUInt.INSTANCE.Lower(@limit),
                        ref _status
                    )
            )
        );
    }

    public SlidingSyncViewBuilder SyncMode(SlidingSyncMode @mode)
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_sync_mode(
                        this.GetHandle(),
                        FfiConverterTypeSlidingSyncMode.INSTANCE.Lower(@mode),
                        ref _status
                    )
            )
        );
    }

    public SlidingSyncViewBuilder BatchSize(UInt32 @size)
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_batch_size(
                        this.GetHandle(),
                        FfiConverterUInt.INSTANCE.Lower(@size),
                        ref _status
                    )
            )
        );
    }

    public SlidingSyncViewBuilder Name(String @name)
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_name(
                        this.GetHandle(),
                        FfiConverterString.INSTANCE.Lower(@name),
                        ref _status
                    )
            )
        );
    }

    public SlidingSyncViewBuilder Sort(List<String> @sort)
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_sort(
                        this.GetHandle(),
                        FfiConverterSequenceString.INSTANCE.Lower(@sort),
                        ref _status
                    )
            )
        );
    }

    public SlidingSyncViewBuilder AddRange(UInt32 @from, UInt32 @to)
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_add_range(
                        this.GetHandle(),
                        FfiConverterUInt.INSTANCE.Lower(@from),
                        FfiConverterUInt.INSTANCE.Lower(@to),
                        ref _status
                    )
            )
        );
    }

    public SlidingSyncViewBuilder ResetRanges()
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_reset_ranges(this.GetHandle(), ref _status)
            )
        );
    }

    public SlidingSyncViewBuilder RequiredState(List<RequiredState> @requiredState)
    {
        return FfiConverterTypeSlidingSyncViewBuilder.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_required_state(
                        this.GetHandle(),
                        FfiConverterSequenceTypeRequiredState.INSTANCE.Lower(@requiredState),
                        ref _status
                    )
            )
        );
    }

    /// <exception cref="ClientException"></exception>
    public SlidingSyncView Build()
    {
        return FfiConverterTypeSlidingSyncView.INSTANCE.Lift(
            _UniffiHelpers.RustCallWithError(
                FfiConverterTypeClientError.INSTANCE,
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_SlidingSyncViewBuilder_build(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeSlidingSyncViewBuilder : FfiConverter<SlidingSyncViewBuilder, SlidingSyncViewBuilderSafeHandle>
{
    public static FfiConverterTypeSlidingSyncViewBuilder INSTANCE = new FfiConverterTypeSlidingSyncViewBuilder();

    public override SlidingSyncViewBuilderSafeHandle Lower(SlidingSyncViewBuilder value)
    {
        return value.GetHandle();
    }

    public override SlidingSyncViewBuilder Lift(SlidingSyncViewBuilderSafeHandle value)
    {
        return new SlidingSyncViewBuilder(value);
    }

    public override SlidingSyncViewBuilder Read(BigEndianStream stream)
    {
        return Lift(new SlidingSyncViewBuilderSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(SlidingSyncViewBuilder value)
    {
        return 8;
    }

    public override void Write(SlidingSyncViewBuilder value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

/// <summary>
/// Cancels on drop
/// </summary>
public interface IStoppableSpawn
{
    Boolean IsCancelled();

    void Cancel();
}

public class StoppableSpawnSafeHandle : FFISafeHandle
{
    public StoppableSpawnSafeHandle()
        : base() { }

    public StoppableSpawnSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_StoppableSpawn_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

/// <summary>
/// Cancels on drop
/// </summary>
public class StoppableSpawn : FFIObject<StoppableSpawnSafeHandle>, IStoppableSpawn
{
    public StoppableSpawn(StoppableSpawnSafeHandle pointer)
        : base(pointer) { }

    public Boolean IsCancelled()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_StoppableSpawn_is_cancelled(this.GetHandle(), ref _status)
            )
        );
    }

    public void Cancel()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus _status) =>
                _UniFFILib.matrix_sdk_ffi_397b_StoppableSpawn_cancel(this.GetHandle(), ref _status)
        );
    }
}

class FfiConverterTypeStoppableSpawn : FfiConverter<StoppableSpawn, StoppableSpawnSafeHandle>
{
    public static FfiConverterTypeStoppableSpawn INSTANCE = new FfiConverterTypeStoppableSpawn();

    public override StoppableSpawnSafeHandle Lower(StoppableSpawn value)
    {
        return value.GetHandle();
    }

    public override StoppableSpawn Lift(StoppableSpawnSafeHandle value)
    {
        return new StoppableSpawn(value);
    }

    public override StoppableSpawn Read(BigEndianStream stream)
    {
        return Lift(new StoppableSpawnSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(StoppableSpawn value)
    {
        return 8;
    }

    public override void Write(StoppableSpawn value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface ITextMessage
{
    BaseMessage BaseMessage();

    String? HtmlBody();
}

public class TextMessageSafeHandle : FFISafeHandle
{
    public TextMessageSafeHandle()
        : base() { }

    public TextMessageSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_TextMessage_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class TextMessage : FFIObject<TextMessageSafeHandle>, ITextMessage
{
    public TextMessage(TextMessageSafeHandle pointer)
        : base(pointer) { }

    public BaseMessage BaseMessage()
    {
        return FfiConverterTypeBaseMessage.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_TextMessage_base_message(this.GetHandle(), ref _status)
            )
        );
    }

    public String? HtmlBody()
    {
        return FfiConverterOptionalString.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_TextMessage_html_body(this.GetHandle(), ref _status)
            )
        );
    }
}

class FfiConverterTypeTextMessage : FfiConverter<TextMessage, TextMessageSafeHandle>
{
    public static FfiConverterTypeTextMessage INSTANCE = new FfiConverterTypeTextMessage();

    public override TextMessageSafeHandle Lower(TextMessage value)
    {
        return value.GetHandle();
    }

    public override TextMessage Lift(TextMessageSafeHandle value)
    {
        return new TextMessage(value);
    }

    public override TextMessage Read(BigEndianStream stream)
    {
        return Lift(new TextMessageSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(TextMessage value)
    {
        return 8;
    }

    public override void Write(TextMessage value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public interface IUnreadNotificationsCount
{
    Boolean HasNotifications();

    UInt32 HighlightCount();

    UInt32 NotificationCount();
}

public class UnreadNotificationsCountSafeHandle : FFISafeHandle
{
    public UnreadNotificationsCountSafeHandle()
        : base() { }

    public UnreadNotificationsCountSafeHandle(IntPtr pointer)
        : base(pointer) { }

    protected override bool ReleaseHandle()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_UnreadNotificationsCount_object_free(this.handle, ref status);
            }
        );
        return true;
    }
}

public class UnreadNotificationsCount : FFIObject<UnreadNotificationsCountSafeHandle>, IUnreadNotificationsCount
{
    public UnreadNotificationsCount(UnreadNotificationsCountSafeHandle pointer)
        : base(pointer) { }

    public Boolean HasNotifications()
    {
        return FfiConverterBoolean.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_UnreadNotificationsCount_has_notifications(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }

    public UInt32 HighlightCount()
    {
        return FfiConverterUInt.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_UnreadNotificationsCount_highlight_count(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }

    public UInt32 NotificationCount()
    {
        return FfiConverterUInt.INSTANCE.Lift(
            _UniffiHelpers.RustCall(
                (ref RustCallStatus _status) =>
                    _UniFFILib.matrix_sdk_ffi_397b_UnreadNotificationsCount_notification_count(
                        this.GetHandle(),
                        ref _status
                    )
            )
        );
    }
}

class FfiConverterTypeUnreadNotificationsCount
    : FfiConverter<UnreadNotificationsCount, UnreadNotificationsCountSafeHandle>
{
    public static FfiConverterTypeUnreadNotificationsCount INSTANCE = new FfiConverterTypeUnreadNotificationsCount();

    public override UnreadNotificationsCountSafeHandle Lower(UnreadNotificationsCount value)
    {
        return value.GetHandle();
    }

    public override UnreadNotificationsCount Lift(UnreadNotificationsCountSafeHandle value)
    {
        return new UnreadNotificationsCount(value);
    }

    public override UnreadNotificationsCount Read(BigEndianStream stream)
    {
        return Lift(new UnreadNotificationsCountSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(UnreadNotificationsCount value)
    {
        return 8;
    }

    public override void Write(UnreadNotificationsCount value, BigEndianStream stream)
    {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}

public record RequiredState(String @key, String @value) { }

class FfiConverterTypeRequiredState : FfiConverterRustBuffer<RequiredState>
{
    public static FfiConverterTypeRequiredState INSTANCE = new FfiConverterTypeRequiredState();

    public override RequiredState Read(BigEndianStream stream)
    {
        return new RequiredState(FfiConverterString.INSTANCE.Read(stream), FfiConverterString.INSTANCE.Read(stream));
    }

    public override int AllocationSize(RequiredState value)
    {
        return FfiConverterString.INSTANCE.AllocationSize(value.@key)
            + FfiConverterString.INSTANCE.AllocationSize(value.@value);
    }

    public override void Write(RequiredState value, BigEndianStream stream)
    {
        FfiConverterString.INSTANCE.Write(value.@key, stream);
        FfiConverterString.INSTANCE.Write(value.@value, stream);
    }
}

public record RoomSubscription(List<RequiredState>? @requiredState, UInt32? @timelineLimit) { }

class FfiConverterTypeRoomSubscription : FfiConverterRustBuffer<RoomSubscription>
{
    public static FfiConverterTypeRoomSubscription INSTANCE = new FfiConverterTypeRoomSubscription();

    public override RoomSubscription Read(BigEndianStream stream)
    {
        return new RoomSubscription(
            FfiConverterOptionalSequenceTypeRequiredState.INSTANCE.Read(stream),
            FfiConverterOptionalUInt.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(RoomSubscription value)
    {
        return FfiConverterOptionalSequenceTypeRequiredState.INSTANCE.AllocationSize(value.@requiredState)
            + FfiConverterOptionalUInt.INSTANCE.AllocationSize(value.@timelineLimit);
    }

    public override void Write(RoomSubscription value, BigEndianStream stream)
    {
        FfiConverterOptionalSequenceTypeRequiredState.INSTANCE.Write(value.@requiredState, stream);
        FfiConverterOptionalUInt.INSTANCE.Write(value.@timelineLimit, stream);
    }
}

public record UpdateSummary(List<String> @views, List<String> @rooms) { }

class FfiConverterTypeUpdateSummary : FfiConverterRustBuffer<UpdateSummary>
{
    public static FfiConverterTypeUpdateSummary INSTANCE = new FfiConverterTypeUpdateSummary();

    public override UpdateSummary Read(BigEndianStream stream)
    {
        return new UpdateSummary(
            FfiConverterSequenceString.INSTANCE.Read(stream),
            FfiConverterSequenceString.INSTANCE.Read(stream)
        );
    }

    public override int AllocationSize(UpdateSummary value)
    {
        return FfiConverterSequenceString.INSTANCE.AllocationSize(value.@views)
            + FfiConverterSequenceString.INSTANCE.AllocationSize(value.@rooms);
    }

    public override void Write(UpdateSummary value, BigEndianStream stream)
    {
        FfiConverterSequenceString.INSTANCE.Write(value.@views, stream);
        FfiConverterSequenceString.INSTANCE.Write(value.@rooms, stream);
    }
}

public enum Membership : int
{
    INVITED,
    JOINED,
    LEFT
}

class FfiConverterTypeMembership : FfiConverterRustBuffer<Membership>
{
    public static FfiConverterTypeMembership INSTANCE = new FfiConverterTypeMembership();

    public override Membership Read(BigEndianStream stream)
    {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(Membership), value))
        {
            return (Membership)value;
        }
        else
        {
            throw new InternalException(
                String.Format("invalid enum value '{}' in FfiConverterTypeMembership.Read()", value)
            );
        }
    }

    public override int AllocationSize(Membership value)
    {
        return 4;
    }

    public override void Write(Membership value, BigEndianStream stream)
    {
        stream.WriteInt((int)value + 1);
    }
}

public record RoomListEntry
{
    public record Empty : RoomListEntry { }

    public record Invalidated(String @roomId) : RoomListEntry { }

    public record Filled(String @roomId) : RoomListEntry { }
}

class FfiConverterTypeRoomListEntry : FfiConverterRustBuffer<RoomListEntry>
{
    public static FfiConverterRustBuffer<RoomListEntry> INSTANCE = new FfiConverterTypeRoomListEntry();

    public override RoomListEntry Read(BigEndianStream stream)
    {
        var value = stream.ReadInt();
        switch (value)
        {
            case 1:
                return new RoomListEntry.Empty();
            case 2:
                return new RoomListEntry.Invalidated(FfiConverterString.INSTANCE.Read(stream));
            case 3:
                return new RoomListEntry.Filled(FfiConverterString.INSTANCE.Read(stream));
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeRoomListEntry.Read()", value)
                );
        }
    }

    public override int AllocationSize(RoomListEntry value)
    {
        switch (value)
        {
            case RoomListEntry.Empty variant_value:
                return 4;
            case RoomListEntry.Invalidated variant_value:
                return 4 + FfiConverterString.INSTANCE.AllocationSize(variant_value.@roomId);
            case RoomListEntry.Filled variant_value:
                return 4 + FfiConverterString.INSTANCE.AllocationSize(variant_value.@roomId);
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeRoomListEntry.AllocationSize()", value)
                );
        }
    }

    public override void Write(RoomListEntry value, BigEndianStream stream)
    {
        switch (value)
        {
            case RoomListEntry.Empty variant_value:
                stream.WriteInt(1);
                break;
            case RoomListEntry.Invalidated variant_value:
                stream.WriteInt(2);
                FfiConverterString.INSTANCE.Write(variant_value.@roomId, stream);
                break;
            case RoomListEntry.Filled variant_value:
                stream.WriteInt(3);
                FfiConverterString.INSTANCE.Write(variant_value.@roomId, stream);
                break;
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeRoomListEntry.Write()", value)
                );
        }
    }
}

public enum SlidingSyncMode : int
{
    /// <summary>
    /// Sync up the entire room list first
    /// </summary>
    FULL_SYNC,

    /// <summary>
    /// Only ever sync the currently selected window
    /// </summary>
    SELECTIVE
}

class FfiConverterTypeSlidingSyncMode : FfiConverterRustBuffer<SlidingSyncMode>
{
    public static FfiConverterTypeSlidingSyncMode INSTANCE = new FfiConverterTypeSlidingSyncMode();

    public override SlidingSyncMode Read(BigEndianStream stream)
    {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(SlidingSyncMode), value))
        {
            return (SlidingSyncMode)value;
        }
        else
        {
            throw new InternalException(
                String.Format("invalid enum value '{}' in FfiConverterTypeSlidingSyncMode.Read()", value)
            );
        }
    }

    public override int AllocationSize(SlidingSyncMode value)
    {
        return 4;
    }

    public override void Write(SlidingSyncMode value, BigEndianStream stream)
    {
        stream.WriteInt((int)value + 1);
    }
}

public enum SlidingSyncState : int
{
    /// <summary>
    /// Hasn't started yet
    /// </summary>
    COLD,

    /// <summary>
    /// We are quickly preloading a preview of the most important rooms
    /// </summary>
    PRELOAD,

    /// <summary>
    /// We are trying to load all remaining rooms, might be in batches
    /// </summary>
    CATCHING_UP,

    /// <summary>
    /// We are all caught up and now only sync the live responses.
    /// </summary>
    LIVE
}

class FfiConverterTypeSlidingSyncState : FfiConverterRustBuffer<SlidingSyncState>
{
    public static FfiConverterTypeSlidingSyncState INSTANCE = new FfiConverterTypeSlidingSyncState();

    public override SlidingSyncState Read(BigEndianStream stream)
    {
        var value = stream.ReadInt() - 1;
        if (Enum.IsDefined(typeof(SlidingSyncState), value))
        {
            return (SlidingSyncState)value;
        }
        else
        {
            throw new InternalException(
                String.Format("invalid enum value '{}' in FfiConverterTypeSlidingSyncState.Read()", value)
            );
        }
    }

    public override int AllocationSize(SlidingSyncState value)
    {
        return 4;
    }

    public override void Write(SlidingSyncState value, BigEndianStream stream)
    {
        stream.WriteInt((int)value + 1);
    }
}

public record SlidingSyncViewRoomsListDiff
{
    public record Replace(List<RoomListEntry> @values) : SlidingSyncViewRoomsListDiff { }

    public record InsertAt(UInt32 @index, RoomListEntry @value) : SlidingSyncViewRoomsListDiff { }

    public record UpdateAt(UInt32 @index, RoomListEntry @value) : SlidingSyncViewRoomsListDiff { }

    public record RemoveAt(UInt32 @index) : SlidingSyncViewRoomsListDiff { }

    public record Move(UInt32 @oldIndex, UInt32 @newIndex) : SlidingSyncViewRoomsListDiff { }

    public record Push(RoomListEntry @value) : SlidingSyncViewRoomsListDiff { }
}

class FfiConverterTypeSlidingSyncViewRoomsListDiff : FfiConverterRustBuffer<SlidingSyncViewRoomsListDiff>
{
    public static FfiConverterRustBuffer<SlidingSyncViewRoomsListDiff> INSTANCE =
        new FfiConverterTypeSlidingSyncViewRoomsListDiff();

    public override SlidingSyncViewRoomsListDiff Read(BigEndianStream stream)
    {
        var value = stream.ReadInt();
        switch (value)
        {
            case 1:
                return new SlidingSyncViewRoomsListDiff.Replace(
                    FfiConverterSequenceTypeRoomListEntry.INSTANCE.Read(stream)
                );
            case 2:
                return new SlidingSyncViewRoomsListDiff.InsertAt(
                    FfiConverterUInt.INSTANCE.Read(stream),
                    FfiConverterTypeRoomListEntry.INSTANCE.Read(stream)
                );
            case 3:
                return new SlidingSyncViewRoomsListDiff.UpdateAt(
                    FfiConverterUInt.INSTANCE.Read(stream),
                    FfiConverterTypeRoomListEntry.INSTANCE.Read(stream)
                );
            case 4:
                return new SlidingSyncViewRoomsListDiff.RemoveAt(FfiConverterUInt.INSTANCE.Read(stream));
            case 5:
                return new SlidingSyncViewRoomsListDiff.Move(
                    FfiConverterUInt.INSTANCE.Read(stream),
                    FfiConverterUInt.INSTANCE.Read(stream)
                );
            case 6:
                return new SlidingSyncViewRoomsListDiff.Push(FfiConverterTypeRoomListEntry.INSTANCE.Read(stream));
            default:
                throw new InternalException(
                    String.Format(
                        "invalid enum value '{}' in FfiConverterTypeSlidingSyncViewRoomsListDiff.Read()",
                        value
                    )
                );
        }
    }

    public override int AllocationSize(SlidingSyncViewRoomsListDiff value)
    {
        switch (value)
        {
            case SlidingSyncViewRoomsListDiff.Replace variant_value:
                return 4 + FfiConverterSequenceTypeRoomListEntry.INSTANCE.AllocationSize(variant_value.@values);
            case SlidingSyncViewRoomsListDiff.InsertAt variant_value:
                return 4
                    + FfiConverterUInt.INSTANCE.AllocationSize(variant_value.@index)
                    + FfiConverterTypeRoomListEntry.INSTANCE.AllocationSize(variant_value.@value);
            case SlidingSyncViewRoomsListDiff.UpdateAt variant_value:
                return 4
                    + FfiConverterUInt.INSTANCE.AllocationSize(variant_value.@index)
                    + FfiConverterTypeRoomListEntry.INSTANCE.AllocationSize(variant_value.@value);
            case SlidingSyncViewRoomsListDiff.RemoveAt variant_value:
                return 4 + FfiConverterUInt.INSTANCE.AllocationSize(variant_value.@index);
            case SlidingSyncViewRoomsListDiff.Move variant_value:
                return 4
                    + FfiConverterUInt.INSTANCE.AllocationSize(variant_value.@oldIndex)
                    + FfiConverterUInt.INSTANCE.AllocationSize(variant_value.@newIndex);
            case SlidingSyncViewRoomsListDiff.Push variant_value:
                return 4 + FfiConverterTypeRoomListEntry.INSTANCE.AllocationSize(variant_value.@value);
            default:
                throw new InternalException(
                    String.Format(
                        "invalid enum value '{}' in FfiConverterTypeSlidingSyncViewRoomsListDiff.AllocationSize()",
                        value
                    )
                );
        }
    }

    public override void Write(SlidingSyncViewRoomsListDiff value, BigEndianStream stream)
    {
        switch (value)
        {
            case SlidingSyncViewRoomsListDiff.Replace variant_value:
                stream.WriteInt(1);
                FfiConverterSequenceTypeRoomListEntry.INSTANCE.Write(variant_value.@values, stream);
                break;
            case SlidingSyncViewRoomsListDiff.InsertAt variant_value:
                stream.WriteInt(2);
                FfiConverterUInt.INSTANCE.Write(variant_value.@index, stream);
                FfiConverterTypeRoomListEntry.INSTANCE.Write(variant_value.@value, stream);
                break;
            case SlidingSyncViewRoomsListDiff.UpdateAt variant_value:
                stream.WriteInt(3);
                FfiConverterUInt.INSTANCE.Write(variant_value.@index, stream);
                FfiConverterTypeRoomListEntry.INSTANCE.Write(variant_value.@value, stream);
                break;
            case SlidingSyncViewRoomsListDiff.RemoveAt variant_value:
                stream.WriteInt(4);
                FfiConverterUInt.INSTANCE.Write(variant_value.@index, stream);
                break;
            case SlidingSyncViewRoomsListDiff.Move variant_value:
                stream.WriteInt(5);
                FfiConverterUInt.INSTANCE.Write(variant_value.@oldIndex, stream);
                FfiConverterUInt.INSTANCE.Write(variant_value.@newIndex, stream);
                break;
            case SlidingSyncViewRoomsListDiff.Push variant_value:
                stream.WriteInt(6);
                FfiConverterTypeRoomListEntry.INSTANCE.Write(variant_value.@value, stream);
                break;
            default:
                throw new InternalException(
                    String.Format(
                        "invalid enum value '{}' in FfiConverterTypeSlidingSyncViewRoomsListDiff.Write()",
                        value
                    )
                );
        }
    }
}

public class AuthenticationException : UniffiException
{
    AuthenticationException(string message)
        : base(message) { }

    // Each variant is a nested class
    // Flat enums carries a string error message, so no special implementation is necessary.

    public class ClientMissing : AuthenticationException
    {
        public ClientMissing(string message)
            : base(message) { }
    }

    public class SessionMissing : AuthenticationException
    {
        public SessionMissing(string message)
            : base(message) { }
    }

    public class Generic : AuthenticationException
    {
        public Generic(string message)
            : base(message) { }
    }
}

class FfiConverterTypeAuthenticationError
    : FfiConverterRustBuffer<AuthenticationException>,
        CallStatusErrorHandler<AuthenticationException>
{
    public static FfiConverterTypeAuthenticationError INSTANCE = new FfiConverterTypeAuthenticationError();

    public override AuthenticationException Read(BigEndianStream stream)
    {
        var value = stream.ReadInt();
        switch (value)
        {
            case 1:
                return new AuthenticationException.ClientMissing(FfiConverterString.INSTANCE.Read(stream));
            case 2:
                return new AuthenticationException.SessionMissing(FfiConverterString.INSTANCE.Read(stream));
            case 3:
                return new AuthenticationException.Generic(FfiConverterString.INSTANCE.Read(stream));
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeAuthenticationError.Read()", value)
                );
        }
    }

    public override int AllocationSize(AuthenticationException value)
    {
        return 4 + FfiConverterString.INSTANCE.AllocationSize(value.Message);
    }

    public override void Write(AuthenticationException value, BigEndianStream stream)
    {
        switch (value)
        {
            case AuthenticationException.ClientMissing:
                stream.WriteInt(1);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case AuthenticationException.SessionMissing:
                stream.WriteInt(2);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            case AuthenticationException.Generic:
                stream.WriteInt(3);
                FfiConverterString.INSTANCE.Write(value.Message, stream);
                break;
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeAuthenticationError.Write()", value)
                );
        }
    }
}

public class ClientException : UniffiException
{
    // Each variant is a nested class


    public class Generic : ClientException
    {
        // Members
        public String @msg;

        // Constructor
        public Generic(String @msg)
        {
            this.@msg = @msg;
        }
    }
}

class FfiConverterTypeClientError : FfiConverterRustBuffer<ClientException>, CallStatusErrorHandler<ClientException>
{
    public static FfiConverterTypeClientError INSTANCE = new FfiConverterTypeClientError();

    public override ClientException Read(BigEndianStream stream)
    {
        var value = stream.ReadInt();
        switch (value)
        {
            case 1:
                return new ClientException.Generic(FfiConverterString.INSTANCE.Read(stream));
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeClientError.Read()", value)
                );
        }
    }

    public override int AllocationSize(ClientException value)
    {
        switch (value)
        {
            case ClientException.Generic variant_value:
                return 4 + FfiConverterString.INSTANCE.AllocationSize(variant_value.@msg);
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeClientError.AllocationSize()", value)
                );
        }
    }

    public override void Write(ClientException value, BigEndianStream stream)
    {
        switch (value)
        {
            case ClientException.Generic variant_value:
                stream.WriteInt(1);
                FfiConverterString.INSTANCE.Write(variant_value.@msg, stream);
                break;
            default:
                throw new InternalException(
                    String.Format("invalid enum value '{}' in FfiConverterTypeClientError.Write()", value)
                );
        }
    }
}

class ConcurrentHandleMap<T>
    where T : notnull
{
    Dictionary<ulong, T> leftMap = new Dictionary<ulong, T>();
    Dictionary<T, ulong> rightMap = new Dictionary<T, ulong>();

    Object lock_ = new Object();
    ulong currentHandle = 0;

    public ulong Insert(T obj)
    {
        lock (lock_)
        {
            ulong existingHandle = 0;
            if (rightMap.TryGetValue(obj, out existingHandle))
            {
                return existingHandle;
            }
            currentHandle += 1;
            leftMap[currentHandle] = obj;
            rightMap[obj] = currentHandle;
            return currentHandle;
        }
    }

    public bool TryGet(ulong handle, out T result)
    {
        // Possible null reference assignment
#pragma warning disable 8601
        return leftMap.TryGetValue(handle, out result);
#pragma warning restore 8601
    }

    public bool Remove(ulong handle)
    {
        return Remove(handle, out T result);
    }

    public bool Remove(ulong handle, out T result)
    {
        lock (lock_)
        {
            // Possible null reference assignment
#pragma warning disable 8601
            if (leftMap.Remove(handle, out result))
            {
#pragma warning restore 8601
                rightMap.Remove(result);
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ForeignCallback(ulong handle, int method, RustBuffer args, ref RustBuffer outBuf);

internal abstract class FfiConverterCallbackInterface<CallbackInterface> : FfiConverter<CallbackInterface, ulong>
    where CallbackInterface : notnull
{
    ConcurrentHandleMap<CallbackInterface> handleMap = new ConcurrentHandleMap<CallbackInterface>();

    // Registers the foreign callback with the Rust side.
    // This method is generated for each callback interface.
    public abstract void Register();

    public RustBuffer Drop(ulong handle)
    {
        handleMap.Remove(handle);
        return new RustBuffer();
    }

    public override CallbackInterface Lift(ulong handle)
    {
        if (!handleMap.TryGet(handle, out CallbackInterface result))
        {
            throw new InternalException($"No callback in handlemap '{handle}'");
        }
        return result;
    }

    public override CallbackInterface Read(BigEndianStream stream)
    {
        return Lift(stream.ReadULong());
    }

    public override ulong Lower(CallbackInterface value)
    {
        return handleMap.Insert(value);
    }

    public override int AllocationSize(CallbackInterface value)
    {
        return 8;
    }

    public override void Write(CallbackInterface value, BigEndianStream stream)
    {
        stream.WriteULong(Lower(value));
    }
}

public interface ClientDelegate
{
    void DidReceiveSyncUpdate();
    void DidReceiveAuthError(Boolean @isSoftLogout);
    void DidUpdateRestoreToken();
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeClientDelegate
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeClientDelegate.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeClientDelegate.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveSyncUpdate(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            case 2:
            {
                try
                {
                    outBuf = InvokeDidReceiveAuthError(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            case 3:
            {
                try
                {
                    outBuf = InvokeDidUpdateRestoreToken(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveSyncUpdate(ClientDelegate callback, RustBuffer args)
    {
        try
        {
            callback.DidReceiveSyncUpdate();

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }

    static RustBuffer InvokeDidReceiveAuthError(ClientDelegate callback, RustBuffer args)
    {
        try
        {
            var stream = args.AsStream();
            callback.DidReceiveAuthError(FfiConverterBoolean.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }

    static RustBuffer InvokeDidUpdateRestoreToken(ClientDelegate callback, RustBuffer args)
    {
        try
        {
            callback.DidUpdateRestoreToken();

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeClientDelegate : FfiConverterCallbackInterface<ClientDelegate>
{
    public static FfiConverterTypeClientDelegate INSTANCE = new FfiConverterTypeClientDelegate();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_ClientDelegate_init_callback(
                    ForeignCallbackTypeClientDelegate.INSTANCE,
                    ref status
                );
            }
        );
    }
}

public interface RoomDelegate
{
    void DidReceiveMessage(AnyMessage @message);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeRoomDelegate
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeRoomDelegate.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeRoomDelegate.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveMessage(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveMessage(RoomDelegate callback, RustBuffer args)
    {
        try
        {
            var stream = args.AsStream();
            callback.DidReceiveMessage(FfiConverterTypeAnyMessage.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeRoomDelegate : FfiConverterCallbackInterface<RoomDelegate>
{
    public static FfiConverterTypeRoomDelegate INSTANCE = new FfiConverterTypeRoomDelegate();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_RoomDelegate_init_callback(
                    ForeignCallbackTypeRoomDelegate.INSTANCE,
                    ref status
                );
            }
        );
    }
}

public interface SessionVerificationControllerDelegate
{
    void DidReceiveVerificationData(List<SessionVerificationEmoji> @data);
    void DidFail();
    void DidCancel();
    void DidFinish();
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeSessionVerificationControllerDelegate
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeSessionVerificationControllerDelegate.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeSessionVerificationControllerDelegate.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveVerificationData(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            case 2:
            {
                try
                {
                    outBuf = InvokeDidFail(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            case 3:
            {
                try
                {
                    outBuf = InvokeDidCancel(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            case 4:
            {
                try
                {
                    outBuf = InvokeDidFinish(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveVerificationData(SessionVerificationControllerDelegate callback, RustBuffer args)
    {
        try
        {
            var stream = args.AsStream();
            callback.DidReceiveVerificationData(FfiConverterSequenceTypeSessionVerificationEmoji.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }

    static RustBuffer InvokeDidFail(SessionVerificationControllerDelegate callback, RustBuffer args)
    {
        try
        {
            callback.DidFail();

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }

    static RustBuffer InvokeDidCancel(SessionVerificationControllerDelegate callback, RustBuffer args)
    {
        try
        {
            callback.DidCancel();

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }

    static RustBuffer InvokeDidFinish(SessionVerificationControllerDelegate callback, RustBuffer args)
    {
        try
        {
            callback.DidFinish();

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeSessionVerificationControllerDelegate
    : FfiConverterCallbackInterface<SessionVerificationControllerDelegate>
{
    public static FfiConverterTypeSessionVerificationControllerDelegate INSTANCE =
        new FfiConverterTypeSessionVerificationControllerDelegate();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SessionVerificationControllerDelegate_init_callback(
                    ForeignCallbackTypeSessionVerificationControllerDelegate.INSTANCE,
                    ref status
                );
            }
        );
    }
}

public interface SlidingSyncObserver
{
    void DidReceiveSyncUpdate(UpdateSummary @summary);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeSlidingSyncObserver
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeSlidingSyncObserver.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeSlidingSyncObserver.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveSyncUpdate(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveSyncUpdate(SlidingSyncObserver callback, RustBuffer args)
    {
        try
        {
            var stream = args.AsStream();
            callback.DidReceiveSyncUpdate(FfiConverterTypeUpdateSummary.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeSlidingSyncObserver : FfiConverterCallbackInterface<SlidingSyncObserver>
{
    public static FfiConverterTypeSlidingSyncObserver INSTANCE = new FfiConverterTypeSlidingSyncObserver();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncObserver_init_callback(
                    ForeignCallbackTypeSlidingSyncObserver.INSTANCE,
                    ref status
                );
            }
        );
    }
}

public interface SlidingSyncViewRoomItemsObserver
{
    void DidReceiveUpdate();
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeSlidingSyncViewRoomItemsObserver
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeSlidingSyncViewRoomItemsObserver.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeSlidingSyncViewRoomItemsObserver.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveUpdate(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveUpdate(SlidingSyncViewRoomItemsObserver callback, RustBuffer args)
    {
        try
        {
            callback.DidReceiveUpdate();

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeSlidingSyncViewRoomItemsObserver : FfiConverterCallbackInterface<SlidingSyncViewRoomItemsObserver>
{
    public static FfiConverterTypeSlidingSyncViewRoomItemsObserver INSTANCE =
        new FfiConverterTypeSlidingSyncViewRoomItemsObserver();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncViewRoomItemsObserver_init_callback(
                    ForeignCallbackTypeSlidingSyncViewRoomItemsObserver.INSTANCE,
                    ref status
                );
            }
        );
    }
}

public interface SlidingSyncViewRoomListObserver
{
    void DidReceiveUpdate(SlidingSyncViewRoomsListDiff @diff);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeSlidingSyncViewRoomListObserver
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeSlidingSyncViewRoomListObserver.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeSlidingSyncViewRoomListObserver.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveUpdate(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveUpdate(SlidingSyncViewRoomListObserver callback, RustBuffer args)
    {
        try
        {
            var stream = args.AsStream();
            callback.DidReceiveUpdate(FfiConverterTypeSlidingSyncViewRoomsListDiff.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeSlidingSyncViewRoomListObserver : FfiConverterCallbackInterface<SlidingSyncViewRoomListObserver>
{
    public static FfiConverterTypeSlidingSyncViewRoomListObserver INSTANCE =
        new FfiConverterTypeSlidingSyncViewRoomListObserver();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncViewRoomListObserver_init_callback(
                    ForeignCallbackTypeSlidingSyncViewRoomListObserver.INSTANCE,
                    ref status
                );
            }
        );
    }
}

public interface SlidingSyncViewRoomsCountObserver
{
    void DidReceiveUpdate(UInt32 @count);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeSlidingSyncViewRoomsCountObserver
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeSlidingSyncViewRoomsCountObserver.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeSlidingSyncViewRoomsCountObserver.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveUpdate(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveUpdate(SlidingSyncViewRoomsCountObserver callback, RustBuffer args)
    {
        try
        {
            var stream = args.AsStream();
            callback.DidReceiveUpdate(FfiConverterUInt.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeSlidingSyncViewRoomsCountObserver
    : FfiConverterCallbackInterface<SlidingSyncViewRoomsCountObserver>
{
    public static FfiConverterTypeSlidingSyncViewRoomsCountObserver INSTANCE =
        new FfiConverterTypeSlidingSyncViewRoomsCountObserver();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncViewRoomsCountObserver_init_callback(
                    ForeignCallbackTypeSlidingSyncViewRoomsCountObserver.INSTANCE,
                    ref status
                );
            }
        );
    }
}

public interface SlidingSyncViewStateObserver
{
    void DidReceiveUpdate(SlidingSyncState @newState);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeSlidingSyncViewStateObserver
{
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) =>
    {
        var cb = FfiConverterTypeSlidingSyncViewStateObserver.INSTANCE.Lift(handle);
        switch (method)
        {
            case 0:
            {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeSlidingSyncViewStateObserver.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            case 1:
            {
                try
                {
                    outBuf = InvokeDidReceiveUpdate(cb, args);
                    return 1;
                }
                catch (Exception e)
                {
                    // Unexpected error
                    try
                    {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    }
                    catch
                    {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            default:
            {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeDidReceiveUpdate(SlidingSyncViewStateObserver callback, RustBuffer args)
    {
        try
        {
            var stream = args.AsStream();
            callback.DidReceiveUpdate(FfiConverterTypeSlidingSyncState.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return new RustBuffer();
        }
        finally
        {
            RustBuffer.Free(args);
        }
    }
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeSlidingSyncViewStateObserver : FfiConverterCallbackInterface<SlidingSyncViewStateObserver>
{
    public static FfiConverterTypeSlidingSyncViewStateObserver INSTANCE =
        new FfiConverterTypeSlidingSyncViewStateObserver();

    public override void Register()
    {
        _UniffiHelpers.RustCall(
            (ref RustCallStatus status) =>
            {
                _UniFFILib.ffi_matrix_sdk_ffi_397b_SlidingSyncViewStateObserver_init_callback(
                    ForeignCallbackTypeSlidingSyncViewStateObserver.INSTANCE,
                    ref status
                );
            }
        );
    }
}

class FfiConverterOptionalUShort : FfiConverterRustBuffer<UInt16?>
{
    public static FfiConverterOptionalUShort INSTANCE = new FfiConverterOptionalUShort();

    public override UInt16? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterUShort.INSTANCE.Read(stream);
    }

    public override int AllocationSize(UInt16? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterUShort.INSTANCE.AllocationSize((UInt16)value);
        }
    }

    public override void Write(UInt16? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterUShort.INSTANCE.Write((UInt16)value, stream);
        }
    }
}

class FfiConverterOptionalUInt : FfiConverterRustBuffer<UInt32?>
{
    public static FfiConverterOptionalUInt INSTANCE = new FfiConverterOptionalUInt();

    public override UInt32? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterUInt.INSTANCE.Read(stream);
    }

    public override int AllocationSize(UInt32? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterUInt.INSTANCE.AllocationSize((UInt32)value);
        }
    }

    public override void Write(UInt32? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterUInt.INSTANCE.Write((UInt32)value, stream);
        }
    }
}

class FfiConverterOptionalULong : FfiConverterRustBuffer<UInt64?>
{
    public static FfiConverterOptionalULong INSTANCE = new FfiConverterOptionalULong();

    public override UInt64? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterULong.INSTANCE.Read(stream);
    }

    public override int AllocationSize(UInt64? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterULong.INSTANCE.AllocationSize((UInt64)value);
        }
    }

    public override void Write(UInt64? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterULong.INSTANCE.Write((UInt64)value, stream);
        }
    }
}

class FfiConverterOptionalBoolean : FfiConverterRustBuffer<Boolean?>
{
    public static FfiConverterOptionalBoolean INSTANCE = new FfiConverterOptionalBoolean();

    public override Boolean? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterBoolean.INSTANCE.Read(stream);
    }

    public override int AllocationSize(Boolean? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterBoolean.INSTANCE.AllocationSize((Boolean)value);
        }
    }

    public override void Write(Boolean? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterBoolean.INSTANCE.Write((Boolean)value, stream);
        }
    }
}

class FfiConverterOptionalString : FfiConverterRustBuffer<String?>
{
    public static FfiConverterOptionalString INSTANCE = new FfiConverterOptionalString();

    public override String? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterString.INSTANCE.Read(stream);
    }

    public override int AllocationSize(String? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterString.INSTANCE.AllocationSize((String)value);
        }
    }

    public override void Write(String? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterString.INSTANCE.Write((String)value, stream);
        }
    }
}

class FfiConverterOptionalTypeAnyMessage : FfiConverterRustBuffer<AnyMessage?>
{
    public static FfiConverterOptionalTypeAnyMessage INSTANCE = new FfiConverterOptionalTypeAnyMessage();

    public override AnyMessage? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeAnyMessage.INSTANCE.Read(stream);
    }

    public override int AllocationSize(AnyMessage? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeAnyMessage.INSTANCE.AllocationSize((AnyMessage)value);
        }
    }

    public override void Write(AnyMessage? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeAnyMessage.INSTANCE.Write((AnyMessage)value, stream);
        }
    }
}

class FfiConverterOptionalTypeBackwardsStream : FfiConverterRustBuffer<BackwardsStream?>
{
    public static FfiConverterOptionalTypeBackwardsStream INSTANCE = new FfiConverterOptionalTypeBackwardsStream();

    public override BackwardsStream? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeBackwardsStream.INSTANCE.Read(stream);
    }

    public override int AllocationSize(BackwardsStream? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeBackwardsStream.INSTANCE.AllocationSize((BackwardsStream)value);
        }
    }

    public override void Write(BackwardsStream? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeBackwardsStream.INSTANCE.Write((BackwardsStream)value, stream);
        }
    }
}

class FfiConverterOptionalTypeEmoteMessage : FfiConverterRustBuffer<EmoteMessage?>
{
    public static FfiConverterOptionalTypeEmoteMessage INSTANCE = new FfiConverterOptionalTypeEmoteMessage();

    public override EmoteMessage? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeEmoteMessage.INSTANCE.Read(stream);
    }

    public override int AllocationSize(EmoteMessage? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeEmoteMessage.INSTANCE.AllocationSize((EmoteMessage)value);
        }
    }

    public override void Write(EmoteMessage? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeEmoteMessage.INSTANCE.Write((EmoteMessage)value, stream);
        }
    }
}

class FfiConverterOptionalTypeHomeserverLoginDetails : FfiConverterRustBuffer<HomeserverLoginDetails?>
{
    public static FfiConverterOptionalTypeHomeserverLoginDetails INSTANCE =
        new FfiConverterOptionalTypeHomeserverLoginDetails();

    public override HomeserverLoginDetails? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeHomeserverLoginDetails.INSTANCE.Read(stream);
    }

    public override int AllocationSize(HomeserverLoginDetails? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeHomeserverLoginDetails.INSTANCE.AllocationSize((HomeserverLoginDetails)value);
        }
    }

    public override void Write(HomeserverLoginDetails? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeHomeserverLoginDetails.INSTANCE.Write((HomeserverLoginDetails)value, stream);
        }
    }
}

class FfiConverterOptionalTypeImageMessage : FfiConverterRustBuffer<ImageMessage?>
{
    public static FfiConverterOptionalTypeImageMessage INSTANCE = new FfiConverterOptionalTypeImageMessage();

    public override ImageMessage? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeImageMessage.INSTANCE.Read(stream);
    }

    public override int AllocationSize(ImageMessage? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeImageMessage.INSTANCE.AllocationSize((ImageMessage)value);
        }
    }

    public override void Write(ImageMessage? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeImageMessage.INSTANCE.Write((ImageMessage)value, stream);
        }
    }
}

class FfiConverterOptionalTypeNoticeMessage : FfiConverterRustBuffer<NoticeMessage?>
{
    public static FfiConverterOptionalTypeNoticeMessage INSTANCE = new FfiConverterOptionalTypeNoticeMessage();

    public override NoticeMessage? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeNoticeMessage.INSTANCE.Read(stream);
    }

    public override int AllocationSize(NoticeMessage? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeNoticeMessage.INSTANCE.AllocationSize((NoticeMessage)value);
        }
    }

    public override void Write(NoticeMessage? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeNoticeMessage.INSTANCE.Write((NoticeMessage)value, stream);
        }
    }
}

class FfiConverterOptionalTypeRoom : FfiConverterRustBuffer<Room?>
{
    public static FfiConverterOptionalTypeRoom INSTANCE = new FfiConverterOptionalTypeRoom();

    public override Room? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeRoom.INSTANCE.Read(stream);
    }

    public override int AllocationSize(Room? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeRoom.INSTANCE.AllocationSize((Room)value);
        }
    }

    public override void Write(Room? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeRoom.INSTANCE.Write((Room)value, stream);
        }
    }
}

class FfiConverterOptionalTypeSlidingSyncRoom : FfiConverterRustBuffer<SlidingSyncRoom?>
{
    public static FfiConverterOptionalTypeSlidingSyncRoom INSTANCE = new FfiConverterOptionalTypeSlidingSyncRoom();

    public override SlidingSyncRoom? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeSlidingSyncRoom.INSTANCE.Read(stream);
    }

    public override int AllocationSize(SlidingSyncRoom? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeSlidingSyncRoom.INSTANCE.AllocationSize((SlidingSyncRoom)value);
        }
    }

    public override void Write(SlidingSyncRoom? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeSlidingSyncRoom.INSTANCE.Write((SlidingSyncRoom)value, stream);
        }
    }
}

class FfiConverterOptionalTypeSlidingSyncView : FfiConverterRustBuffer<SlidingSyncView?>
{
    public static FfiConverterOptionalTypeSlidingSyncView INSTANCE = new FfiConverterOptionalTypeSlidingSyncView();

    public override SlidingSyncView? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeSlidingSyncView.INSTANCE.Read(stream);
    }

    public override int AllocationSize(SlidingSyncView? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeSlidingSyncView.INSTANCE.AllocationSize((SlidingSyncView)value);
        }
    }

    public override void Write(SlidingSyncView? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeSlidingSyncView.INSTANCE.Write((SlidingSyncView)value, stream);
        }
    }
}

class FfiConverterOptionalTypeTextMessage : FfiConverterRustBuffer<TextMessage?>
{
    public static FfiConverterOptionalTypeTextMessage INSTANCE = new FfiConverterOptionalTypeTextMessage();

    public override TextMessage? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeTextMessage.INSTANCE.Read(stream);
    }

    public override int AllocationSize(TextMessage? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeTextMessage.INSTANCE.AllocationSize((TextMessage)value);
        }
    }

    public override void Write(TextMessage? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeTextMessage.INSTANCE.Write((TextMessage)value, stream);
        }
    }
}

class FfiConverterOptionalTypeRoomSubscription : FfiConverterRustBuffer<RoomSubscription?>
{
    public static FfiConverterOptionalTypeRoomSubscription INSTANCE = new FfiConverterOptionalTypeRoomSubscription();

    public override RoomSubscription? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeRoomSubscription.INSTANCE.Read(stream);
    }

    public override int AllocationSize(RoomSubscription? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeRoomSubscription.INSTANCE.AllocationSize((RoomSubscription)value);
        }
    }

    public override void Write(RoomSubscription? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeRoomSubscription.INSTANCE.Write((RoomSubscription)value, stream);
        }
    }
}

class FfiConverterOptionalTypeClientDelegate : FfiConverterRustBuffer<ClientDelegate?>
{
    public static FfiConverterOptionalTypeClientDelegate INSTANCE = new FfiConverterOptionalTypeClientDelegate();

    public override ClientDelegate? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeClientDelegate.INSTANCE.Read(stream);
    }

    public override int AllocationSize(ClientDelegate? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeClientDelegate.INSTANCE.AllocationSize((ClientDelegate)value);
        }
    }

    public override void Write(ClientDelegate? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeClientDelegate.INSTANCE.Write((ClientDelegate)value, stream);
        }
    }
}

class FfiConverterOptionalTypeRoomDelegate : FfiConverterRustBuffer<RoomDelegate?>
{
    public static FfiConverterOptionalTypeRoomDelegate INSTANCE = new FfiConverterOptionalTypeRoomDelegate();

    public override RoomDelegate? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeRoomDelegate.INSTANCE.Read(stream);
    }

    public override int AllocationSize(RoomDelegate? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeRoomDelegate.INSTANCE.AllocationSize((RoomDelegate)value);
        }
    }

    public override void Write(RoomDelegate? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeRoomDelegate.INSTANCE.Write((RoomDelegate)value, stream);
        }
    }
}

class FfiConverterOptionalTypeSessionVerificationControllerDelegate
    : FfiConverterRustBuffer<SessionVerificationControllerDelegate?>
{
    public static FfiConverterOptionalTypeSessionVerificationControllerDelegate INSTANCE =
        new FfiConverterOptionalTypeSessionVerificationControllerDelegate();

    public override SessionVerificationControllerDelegate? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeSessionVerificationControllerDelegate.INSTANCE.Read(stream);
    }

    public override int AllocationSize(SessionVerificationControllerDelegate? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1
                + FfiConverterTypeSessionVerificationControllerDelegate.INSTANCE.AllocationSize(
                    (SessionVerificationControllerDelegate)value
                );
        }
    }

    public override void Write(SessionVerificationControllerDelegate? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeSessionVerificationControllerDelegate.INSTANCE.Write(
                (SessionVerificationControllerDelegate)value,
                stream
            );
        }
    }
}

class FfiConverterOptionalTypeSlidingSyncObserver : FfiConverterRustBuffer<SlidingSyncObserver?>
{
    public static FfiConverterOptionalTypeSlidingSyncObserver INSTANCE =
        new FfiConverterOptionalTypeSlidingSyncObserver();

    public override SlidingSyncObserver? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterTypeSlidingSyncObserver.INSTANCE.Read(stream);
    }

    public override int AllocationSize(SlidingSyncObserver? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterTypeSlidingSyncObserver.INSTANCE.AllocationSize((SlidingSyncObserver)value);
        }
    }

    public override void Write(SlidingSyncObserver? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterTypeSlidingSyncObserver.INSTANCE.Write((SlidingSyncObserver)value, stream);
        }
    }
}

class FfiConverterOptionalSequenceTypeRequiredState : FfiConverterRustBuffer<List<RequiredState>?>
{
    public static FfiConverterOptionalSequenceTypeRequiredState INSTANCE =
        new FfiConverterOptionalSequenceTypeRequiredState();

    public override List<RequiredState>? Read(BigEndianStream stream)
    {
        if (stream.ReadByte() == 0)
        {
            return null;
        }
        return FfiConverterSequenceTypeRequiredState.INSTANCE.Read(stream);
    }

    public override int AllocationSize(List<RequiredState>? value)
    {
        if (value == null)
        {
            return 1;
        }
        else
        {
            return 1 + FfiConverterSequenceTypeRequiredState.INSTANCE.AllocationSize((List<RequiredState>)value);
        }
    }

    public override void Write(List<RequiredState>? value, BigEndianStream stream)
    {
        if (value == null)
        {
            stream.WriteByte(0);
        }
        else
        {
            stream.WriteByte(1);
            FfiConverterSequenceTypeRequiredState.INSTANCE.Write((List<RequiredState>)value, stream);
        }
    }
}

class FfiConverterSequenceByte : FfiConverterRustBuffer<List<Byte>>
{
    public static FfiConverterSequenceByte INSTANCE = new FfiConverterSequenceByte();

    public override List<Byte> Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var result = new List<Byte>(length);
        for (int i = 0; i < length; i++)
        {
            result.Add(FfiConverterByte.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<Byte> value)
    {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterByte.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<Byte> value, BigEndianStream stream)
    {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterByte.INSTANCE.Write(item, stream));
    }
}

class FfiConverterSequenceString : FfiConverterRustBuffer<List<String>>
{
    public static FfiConverterSequenceString INSTANCE = new FfiConverterSequenceString();

    public override List<String> Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var result = new List<String>(length);
        for (int i = 0; i < length; i++)
        {
            result.Add(FfiConverterString.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<String> value)
    {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterString.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<String> value, BigEndianStream stream)
    {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterString.INSTANCE.Write(item, stream));
    }
}

class FfiConverterSequenceTypeAnyMessage : FfiConverterRustBuffer<List<AnyMessage>>
{
    public static FfiConverterSequenceTypeAnyMessage INSTANCE = new FfiConverterSequenceTypeAnyMessage();

    public override List<AnyMessage> Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var result = new List<AnyMessage>(length);
        for (int i = 0; i < length; i++)
        {
            result.Add(FfiConverterTypeAnyMessage.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<AnyMessage> value)
    {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeAnyMessage.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<AnyMessage> value, BigEndianStream stream)
    {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeAnyMessage.INSTANCE.Write(item, stream));
    }
}

class FfiConverterSequenceTypeSessionVerificationEmoji : FfiConverterRustBuffer<List<SessionVerificationEmoji>>
{
    public static FfiConverterSequenceTypeSessionVerificationEmoji INSTANCE =
        new FfiConverterSequenceTypeSessionVerificationEmoji();

    public override List<SessionVerificationEmoji> Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var result = new List<SessionVerificationEmoji>(length);
        for (int i = 0; i < length; i++)
        {
            result.Add(FfiConverterTypeSessionVerificationEmoji.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<SessionVerificationEmoji> value)
    {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            return sizeForLength;
        }

        var sizeForItems = value
            .Select(item => FfiConverterTypeSessionVerificationEmoji.INSTANCE.AllocationSize(item))
            .Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<SessionVerificationEmoji> value, BigEndianStream stream)
    {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeSessionVerificationEmoji.INSTANCE.Write(item, stream));
    }
}

class FfiConverterSequenceTypeRequiredState : FfiConverterRustBuffer<List<RequiredState>>
{
    public static FfiConverterSequenceTypeRequiredState INSTANCE = new FfiConverterSequenceTypeRequiredState();

    public override List<RequiredState> Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var result = new List<RequiredState>(length);
        for (int i = 0; i < length; i++)
        {
            result.Add(FfiConverterTypeRequiredState.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<RequiredState> value)
    {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeRequiredState.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<RequiredState> value, BigEndianStream stream)
    {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeRequiredState.INSTANCE.Write(item, stream));
    }
}

class FfiConverterSequenceTypeRoomListEntry : FfiConverterRustBuffer<List<RoomListEntry>>
{
    public static FfiConverterSequenceTypeRoomListEntry INSTANCE = new FfiConverterSequenceTypeRoomListEntry();

    public override List<RoomListEntry> Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var result = new List<RoomListEntry>(length);
        for (int i = 0; i < length; i++)
        {
            result.Add(FfiConverterTypeRoomListEntry.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<RoomListEntry> value)
    {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            return sizeForLength;
        }

        var sizeForItems = value.Select(item => FfiConverterTypeRoomListEntry.INSTANCE.AllocationSize(item)).Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<RoomListEntry> value, BigEndianStream stream)
    {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterTypeRoomListEntry.INSTANCE.Write(item, stream));
    }
}

class FfiConverterSequenceOptionalTypeSlidingSyncRoom : FfiConverterRustBuffer<List<SlidingSyncRoom?>>
{
    public static FfiConverterSequenceOptionalTypeSlidingSyncRoom INSTANCE =
        new FfiConverterSequenceOptionalTypeSlidingSyncRoom();

    public override List<SlidingSyncRoom?> Read(BigEndianStream stream)
    {
        var length = stream.ReadInt();
        var result = new List<SlidingSyncRoom?>(length);
        for (int i = 0; i < length; i++)
        {
            result.Add(FfiConverterOptionalTypeSlidingSyncRoom.INSTANCE.Read(stream));
        }
        return result;
    }

    public override int AllocationSize(List<SlidingSyncRoom?> value)
    {
        var sizeForLength = 4;

        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            return sizeForLength;
        }

        var sizeForItems = value
            .Select(item => FfiConverterOptionalTypeSlidingSyncRoom.INSTANCE.AllocationSize(item))
            .Sum();
        return sizeForLength + sizeForItems;
    }

    public override void Write(List<SlidingSyncRoom?> value, BigEndianStream stream)
    {
        // details/1-empty-list-as-default-method-parameter.md
        if (value == null)
        {
            stream.WriteInt(0);
            return;
        }

        stream.WriteInt(value.Count);
        value.ForEach(item => FfiConverterOptionalTypeSlidingSyncRoom.INSTANCE.Write(item, stream));
    }
}
#pragma warning restore 8625
public static class MatrixSdkFfiMethods { }
