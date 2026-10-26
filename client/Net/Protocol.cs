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
    public const byte Input = 0x15;
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
    public const byte WorldInit = 0x98;
    public const byte PropSpawn = 0x99;
    public const byte PropState = 0x9A;
    public const byte SelfState = 0x9B;
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
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
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
public sealed record NodeSpawn(uint Id, byte Kind, float X, float Y, float Z) : ServerMessage;
/// <summary>지형 높이(n×n, m 단위)와 나무·바위 배치(x, y, z, 크기)를 4개씩 펼친 배열.</summary>
public sealed record WorldInit(int N, float Size, float[] Heights, float[] Trees, float[] Rocks) : ServerMessage;
public sealed record PropSpawn(uint Id, byte Kind, float[] Size, float[] Pose) : ServerMessage;
/// <summary>움직이는 소품들의 자세: (id, px, py, pz, qx, qy, qz, qw) 를 8개씩 펼친 배열.</summary>
public sealed record PropState(float[] Poses) : ServerMessage;
public sealed record SelfState(float X, float Y, float Z, float Vx, float Vy, float Vz, bool Grounded) : ServerMessage;
public sealed record NodeRemove(uint Id) : ServerMessage;
public sealed record ChatBroadcast(uint Id, string Text) : ServerMessage;

public static class Packets
{
    public static byte[] Register(string phone, byte gender) => new PacketWriter(Op.Register).Str(phone).U8(gender).ToArray();
    public static byte[] RequestCode(string token) => new PacketWriter(Op.RequestCode).Str(token).ToArray();
    public static byte[] Login(string token, string code) => new PacketWriter(Op.Login).Str(token).Str(code).ToArray();
    /// <summary>이동 의도. mx, mz 는 월드 좌표 방향(길이 0..1), flags: 1 점프, 2 달리기.</summary>
    public static byte[] Input(float mx, float mz, float yaw, byte flags) =>
        new PacketWriter(Op.Input).F32(mx).F32(mz).F32(yaw).U8(flags).ToArray();
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
            Op.NodeSpawn => new NodeSpawn(r.U32(), r.U8(), r.F32(), r.F32(), r.F32()),
            Op.WorldInit => ReadWorld(r),
            Op.PropSpawn => new PropSpawn(r.U32(), r.U8(), Floats(r, 3), Floats(r, 7)),
            Op.PropState => ReadProps(r),
            Op.SelfState => new SelfState(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.U8() != 0),
            Op.NodeRemove => new NodeRemove(r.U32()),
            Op.ChatBroadcast => new ChatBroadcast(r.U32(), r.Str()),
            _ => null,
        };
    }

    private static float[] Floats(PacketReader r, int n)
    {
        var a = new float[n];
        for (var i = 0; i < n; i++) a[i] = r.F32();
        return a;
    }

    private static WorldInit ReadWorld(PacketReader r)
    {
        int n = r.U16();
        var size = r.F32();
        var heights = new float[n * n];
        for (var i = 0; i < heights.Length; i++) heights[i] = r.I16() / 100f;
        var trees = Floats(r, r.U16() * 4);
        var rocks = Floats(r, r.U16() * 4);
        return new WorldInit(n, size, heights, trees, rocks);
    }

    private static PropState ReadProps(PacketReader r)
    {
        int count = r.U16();
        var poses = new float[count * 8];
        for (var i = 0; i < count; i++)
        {
            poses[i * 8] = r.U32();
            for (var k = 1; k < 8; k++) poses[i * 8 + k] = r.F32();
        }
        return new PropState(poses);
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
