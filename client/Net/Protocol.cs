using System.Buffers.Binary;
using System.Text;
using DollyForge.Client.Game;

namespace DollyForge.Client.Net;

/// <summary>
/// 바이너리 프로토콜. Rust 서버(server/src/protocol.rs)와 동일한 형식.
/// WebSocket 바이너리 메시지 1개 = [u8 opcode][payload], 모든 값은 little-endian,
/// 문자열은 [u16 바이트 길이][UTF-8].
/// </summary>
public static class Op
{
    // Client -> Server
    public const byte Register = 0x01;
    public const byte RequestCode = 0x02;
    public const byte Login = 0x03;
    public const byte Move = 0x10;
    public const byte Interact = 0x11;
    public const byte Chat = 0x12;
    public const byte Restart = 0x14;

    // Server -> Client
    public const byte Registered = 0x81;
    public const byte CodeSent = 0x82;
    public const byte LoginOk = 0x83;
    public const byte PlayerState = 0x84;
    public const byte Message = 0x85;
    public const byte Ending = 0x86;
    public const byte Teleport = 0x87;
    public const byte Error = 0x8F;
    public const byte PlayerJoined = 0x91;
    public const byte PlayerMoved = 0x92;
    public const byte PlayerLeft = 0x93;
    public const byte NodeSpawn = 0x94;
    public const byte NodeRemove = 0x95;
    public const byte ChatBroadcast = 0x96;
}

public sealed class PacketWriter(byte opcode)
{
    private readonly List<byte> _buf = [opcode];

    public PacketWriter U8(byte v) { _buf.Add(v); return this; }

    public PacketWriter U16(ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        _buf.AddRange(b);
        return this;
    }

    public PacketWriter U32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        _buf.AddRange(b);
        return this;
    }

    public PacketWriter F32(float v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        _buf.AddRange(b);
        return this;
    }

    public PacketWriter Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        U16((ushort)bytes.Length);
        _buf.AddRange(bytes);
        return this;
    }

    public byte[] ToArray() => [.. _buf];
}

public sealed class PacketReader(byte[] data, int offset = 1)
{
    private int _pos = offset;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > data.Length) throw new InvalidDataException("payload too short");
        var s = data.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
}

public enum MessageKind : byte { Toast = 0, Dialogue = 1, System = 2 }

public abstract record ServerMessage;
public sealed record Registered(string Token) : ServerMessage;
public sealed record CodeSent(string MaskedPhone, string DevCode) : ServerMessage;
public sealed record LoginOk(uint PlayerId, byte Gender) : ServerMessage;
public sealed record StateUpdate(PlayerState State) : ServerMessage;
public sealed record TextMessage(MessageKind Kind, string Text) : ServerMessage;
public sealed record EndingMessage(bool Eternal, string Title, string Text) : ServerMessage;
public sealed record Teleport(float X, float Y, float Z, float Yaw) : ServerMessage;
public sealed record ErrorMessage(string Text) : ServerMessage;
public sealed record PlayerJoined(uint Id, byte Gender, float X, float Y, float Z, float Yaw) : ServerMessage;
public sealed record PlayerMoved(uint Id, float X, float Y, float Z, float Yaw) : ServerMessage;
public sealed record PlayerLeft(uint Id) : ServerMessage;
public sealed record NodeSpawn(uint Id, byte Kind, float X, float Z) : ServerMessage;
public sealed record NodeRemove(uint Id) : ServerMessage;
public sealed record ChatBroadcast(uint Id, string Text) : ServerMessage;

public static class Packets
{
    public static byte[] Register(string phone, byte gender) => new PacketWriter(Op.Register).Str(phone).U8(gender).ToArray();
    public static byte[] RequestCode(string token) => new PacketWriter(Op.RequestCode).Str(token).ToArray();
    public static byte[] Login(string token, string code) => new PacketWriter(Op.Login).Str(token).Str(code).ToArray();
    public static byte[] Move(float x, float y, float z, float yaw) => new PacketWriter(Op.Move).F32(x).F32(y).F32(z).F32(yaw).ToArray();
    public static byte[] Interact(uint target) => new PacketWriter(Op.Interact).U32(target).ToArray();
    public static byte[] Chat(string text) => new PacketWriter(Op.Chat).Str(text).ToArray();
    public static byte[] Restart() => [Op.Restart];

    public static ServerMessage? Parse(byte[] data)
    {
        if (data.Length == 0) return null;
        var r = new PacketReader(data);
        return data[0] switch
        {
            Op.Registered => new Registered(r.Str()),
            Op.CodeSent => new CodeSent(r.Str(), r.Str()),
            Op.LoginOk => new LoginOk(r.U32(), r.U8()),
            Op.PlayerState => new StateUpdate(ReadState(r)),
            Op.Message => new TextMessage((MessageKind)r.U8(), r.Str()),
            Op.Ending => new EndingMessage(r.U8() == 0, r.Str(), r.Str()),
            Op.Teleport => new Teleport(r.F32(), r.F32(), r.F32(), r.F32()),
            Op.Error => new ErrorMessage(r.Str()),
            Op.PlayerJoined => new PlayerJoined(r.U32(), r.U8(), r.F32(), r.F32(), r.F32(), r.F32()),
            Op.PlayerMoved => new PlayerMoved(r.U32(), r.F32(), r.F32(), r.F32(), r.F32()),
            Op.PlayerLeft => new PlayerLeft(r.U32()),
            Op.NodeSpawn => new NodeSpawn(r.U32(), r.U8(), r.F32(), r.F32()),
            Op.NodeRemove => new NodeRemove(r.U32()),
            Op.ChatBroadcast => new ChatBroadcast(r.U32(), r.Str()),
            _ => null,
        };
    }

    private static PlayerState ReadState(PacketReader r)
    {
        var day = r.U16();
        var years = r.U16();
        var phase = (Phase)r.U8();
        var tasks = r.U8();
        var battery = r.F32();
        var bio = r.F32();
        var memory = r.U16();
        var inv = new[] { r.U16(), r.U16(), r.U16(), r.U16() };
        return new PlayerState(day, years, phase, tasks, battery, bio, memory, inv,
            Collected: r.U8(), Research: r.U8(), HaramAge: r.U8(), HaramAlive: r.U8() != 0, Gender: r.U8());
    }
}
