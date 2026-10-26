using Microsoft.JSInterop;

namespace DollyForge.Client.Game;

/// <summary>
/// 3D 렌더러(wwwroot/js/engine.js, Three.js + WebXR) 래퍼.
/// 게임 규칙은 C#·Rust 에 있고, JS 는 그리기·입력·충돌만 맡는다.
/// </summary>
public sealed class Engine(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task InitAsync<T>(string hostId, DotNetObjectReference<T> callbacks, byte gender) where T : class
    {
        _module = await js.InvokeAsync<IJSObjectReference>("import", "./js/engine.js");
        var stations = GameData.Stations.Select(s => new { id = s.Id, label = s.Label, x = s.X, z = s.Z });
        await _module.InvokeVoidAsync("init", hostId, callbacks, stations, gender);
    }

    private ValueTask Call(string fn, params object?[] args) =>
        _module is null ? ValueTask.CompletedTask : _module.InvokeVoidAsync(fn, args);

    public ValueTask ShowPreview(byte gender) => Call("showPreview", gender);
    public ValueTask EnterWorld(byte gender) => Call("enterWorld", gender);

    public ValueTask SetWorld(PlayerState s) =>
        Call("setWorld", (int)s.Phase, s.Research, s.Years, s.HaramAlive, s.HaramAge);

    public ValueTask Teleport(float x, float y, float z, float yaw) => Call("teleport", x, y, z, yaw);
    public ValueTask UpsertPlayer(uint id, byte gender, float x, float y, float z, float yaw) =>
        Call("upsertPlayer", id, gender, x, y, z, yaw);
    public ValueTask MovePlayer(uint id, float x, float y, float z, float yaw) => Call("movePlayer", id, x, y, z, yaw);
    public ValueTask RemovePlayer(uint id) => Call("removePlayer", id);
    public ValueTask AddNode(uint id, byte kind, float x, float y, float z) => Call("addNode", id, kind, x, y, z);
    public ValueTask InitWorld(int n, float size, float[] heights, float[] trees, float[] rocks) =>
        Call("initWorld", n, size, heights, trees, rocks);
    public ValueTask AddProp(uint id, byte kind, float[] size, float[] pose) => Call("addProp", id, kind, size, pose);
    public ValueTask SetPropPoses(float[] poses) => Call("setPropPoses", poses);
    public ValueTask SetSelf(float x, float y, float z, float vx, float vy, float vz, bool grounded) =>
        Call("setSelf", x, y, z, vx, vy, vz, grounded);
    public ValueTask RemoveNode(uint id) => Call("removeNode", id);
    public ValueTask ClearWorld() => Call("clearWorld");
    public ValueTask SetVrHud(IEnumerable<string> lines) => Call("setVrHud", lines.ToArray());
    public ValueTask ExitVr() => Call("exitVr");

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
