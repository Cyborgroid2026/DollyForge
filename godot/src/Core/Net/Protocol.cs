using System.Buffers.Binary;
using System.Text;

namespace DollyForge.Core.Net;

/// <summary>
/// 월드 서버 프로토콜. Rust 서버(server/src/protocol.rs)와 같은 형식.
/// WebSocket 바이너리 메시지 1개 = [u8 opcode][payload], 모두 little-endian.
/// 문자열 = [u16 길이][UTF-8], 큰 덩어리(기기 JSON) = [u32 길이][UTF-8].
/// </summary>
public static class Op
{
    // 클라이언트 → 서버
    public const byte Register = 0x01, RequestCode = 0x02, Login = 0x03;
    public const byte Pose = 0x10, Chat = 0x12;
    public const byte Purchase = 0x20, Consume = 0x21, Refund = 0x22, StowDevice = 0x23, ListDevices = 0x24, RetrieveDevice = 0x25, SaveBody = 0x26;

    // 서버 → 클라이언트
    public const byte Registered = 0x81, CodeSent = 0x82, LoginOk = 0x83, Profile = 0x84, Message = 0x85, Error = 0x8F;
    public const byte PlayerJoined = 0x91, PlayerMoved = 0x92, PlayerLeft = 0x93, ChatBroadcast = 0x96;
    public const byte PurchaseResult = 0xA0, DeviceList = 0xA4, DeviceData = 0xA5, BodyData = 0xA6;
}

public sealed class PacketWriter(byte opcode)
{
    readonly List<byte> _buf = [opcode];

    public PacketWriter U8(byte v) { _buf.Add(v); return this; }
    public PacketWriter U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _buf.AddRange(b); return this; }
    public PacketWriter U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _buf.AddRange(b); return this; }
    public PacketWriter I64(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); _buf.AddRange(b); return this; }
    public PacketWriter F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); _buf.AddRange(b); return this; }

    public PacketWriter Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("문자열이 너무 깁니다");
        U16((ushort)bytes.Length);
        _buf.AddRange(bytes);
        return this;
    }

    public PacketWriter Blob(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        U32((uint)bytes.Length);
        _buf.AddRange(bytes);
        return this;
    }

    public byte[] ToArray() => [.. _buf];
}

public sealed class PacketReader(byte[] data, int offset = 1)
{
    int _pos = offset;

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > data.Length) throw new InvalidDataException("payload too short");
        var s = data.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
    public string Blob() => Encoding.UTF8.GetString(Take((int)U32()));
}

public enum MessageKind : byte { Toast = 0, Dialogue = 1, System = 2 }

/// <summary>주문 한 줄 (서버 영수증용): 품번, 수량, 재단 치수 mm, 서버가 계산한 단가</summary>
public sealed record ReceiptLine(string Sku, int Qty, int LengthMm, int WidthMm, int UnitKrw);
public sealed record StoredDevice(string Name, int Parts, int Bytes);

public abstract record ServerMessage;
public sealed record Registered(string Token) : ServerMessage;
public sealed record CodeSent(string MaskedPhone, string DevCode) : ServerMessage;
public sealed record LoginOk(uint PlayerId, byte Gender) : ServerMessage;
public sealed record Profile(long BalanceKrw, Dictionary<string, int> Consumables) : ServerMessage;
public sealed record TextMessage(MessageKind Kind, string Text) : ServerMessage;
public sealed record ErrorMessage(string Text) : ServerMessage;
public sealed record PlayerJoined(uint Id, byte Gender, float X, float Y, float Z, float Yaw) : ServerMessage;
public sealed record PlayerMoved(uint Id, float X, float Y, float Z, float Yaw) : ServerMessage;
public sealed record PlayerLeft(uint Id) : ServerMessage;
public sealed record ChatBroadcast(uint Id, string Text) : ServerMessage;
public sealed record PurchaseResult(bool Ok, string Message, int TotalKrw, long BalanceKrw, List<ReceiptLine> Lines) : ServerMessage;
public sealed record DeviceList(List<StoredDevice> Devices) : ServerMessage;
public sealed record DeviceData(string Name, string Json) : ServerMessage;
/// <summary>저장된 몸 구성: 표준이 아닌 모듈 품번, 몸에 찬 기기 품번</summary>
public sealed record BodyData(List<string> Modules, List<string> Worn) : ServerMessage;

public static class Packets
{
    public static byte[] Register(string phone, byte gender) => new PacketWriter(Op.Register).Str(phone).U8(gender).ToArray();
    public static byte[] RequestCode(string token) => new PacketWriter(Op.RequestCode).Str(token).ToArray();
    public static byte[] Login(string token, string code) => new PacketWriter(Op.Login).Str(token).Str(code).ToArray();
    public static byte[] Pose(float x, float y, float z, float yaw) => new PacketWriter(Op.Pose).F32(x).F32(y).F32(z).F32(yaw).ToArray();
    public static byte[] Chat(string text) => new PacketWriter(Op.Chat).Str(text).ToArray();

    public static byte[] Purchase(IReadOnlyList<OrderLine> lines)
    {
        var w = new PacketWriter(Op.Purchase).U16((ushort)lines.Count);
        foreach (var l in lines) w.Str(l.Item.Sku).U16((ushort)l.Qty).U32((uint)l.LengthMm).U32((uint)l.WidthMm);
        return w.ToArray();
    }

    public static byte[] Consume(string sku, int qty) => new PacketWriter(Op.Consume).Str(sku).U16((ushort)qty).ToArray();
    public static byte[] Refund(string sku, int qty) => new PacketWriter(Op.Refund).Str(sku).U16((ushort)qty).ToArray();
    public static byte[] StowDevice(string name, string json) => new PacketWriter(Op.StowDevice).Str(name).Blob(json).ToArray();
    public static byte[] ListDevices() => [Op.ListDevices];
    public static byte[] RetrieveDevice(string name) => new PacketWriter(Op.RetrieveDevice).Str(name).ToArray();

    public static byte[] SaveBody(IReadOnlyList<string> modules, IReadOnlyList<string> worn)
    {
        var w = new PacketWriter(Op.SaveBody).U16((ushort)modules.Count);
        foreach (var m in modules) w.Str(m);
        w.U16((ushort)worn.Count);
        foreach (var x in worn) w.Str(x);
        return w.ToArray();
    }

    public static ServerMessage? Parse(byte[] data)
    {
        if (data.Length == 0) return null;
        var r = new PacketReader(data);
        return data[0] switch
        {
            Op.Registered => new Registered(r.Str()),
            Op.CodeSent => new CodeSent(r.Str(), r.Str()),
            Op.LoginOk => new LoginOk(r.U32(), r.U8()),
            Op.Profile => ReadProfile(r),
            Op.Message => new TextMessage((MessageKind)r.U8(), r.Str()),
            Op.Error => new ErrorMessage(r.Str()),
            Op.PlayerJoined => new PlayerJoined(r.U32(), r.U8(), r.F32(), r.F32(), r.F32(), r.F32()),
            Op.PlayerMoved => new PlayerMoved(r.U32(), r.F32(), r.F32(), r.F32(), r.F32()),
            Op.PlayerLeft => new PlayerLeft(r.U32()),
            Op.ChatBroadcast => new ChatBroadcast(r.U32(), r.Str()),
            Op.PurchaseResult => ReadPurchase(r),
            Op.DeviceList => ReadDeviceList(r),
            Op.DeviceData => new DeviceData(r.Str(), r.Blob()),
            Op.BodyData => new BodyData(ReadList(r), ReadList(r)),
            _ => null,
        };
    }

    static List<string> ReadList(PacketReader r)
    {
        var n = r.U16();
        var list = new List<string>(n);
        for (var i = 0; i < n; i++) list.Add(r.Str());
        return list;
    }

    static Profile ReadProfile(PacketReader r)
    {
        var balance = r.I64();
        var n = r.U16();
        var stock = new Dictionary<string, int>();
        for (var i = 0; i < n; i++) stock[r.Str()] = (int)r.U32();
        return new Profile(balance, stock);
    }

    static PurchaseResult ReadPurchase(PacketReader r)
    {
        var ok = r.U8() != 0;
        var msg = r.Str();
        var total = (int)r.U32();
        var balance = r.I64();
        var n = r.U16();
        var lines = new List<ReceiptLine>();
        for (var i = 0; i < n; i++) lines.Add(new(r.Str(), r.U16(), (int)r.U32(), (int)r.U32(), (int)r.U32()));
        return new PurchaseResult(ok, msg, total, balance, lines);
    }

    static DeviceList ReadDeviceList(PacketReader r)
    {
        var n = r.U16();
        var list = new List<StoredDevice>();
        for (var i = 0; i < n; i++) list.Add(new(r.Str(), r.U16(), (int)r.U32()));
        return new DeviceList(list);
    }
}
