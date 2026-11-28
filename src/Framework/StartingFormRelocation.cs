using System.Reflection;
using UObject = UnityEngine.Object;

namespace Battlemarked.ModFramework;

// Read-only native board adapter. The source is an ordinary 1x1 local hero;
// the entire destination form must fit at the native center and orientation.
internal sealed class StartingFormRelocation : IStartingFormTerrain
{
    private readonly object board, tiles, pieceId, footprint;
    private readonly ConstructorInfo pointCtor, rectCtor;
    private readonly MethodInfo exists, tileAt, wall, occupied, walkable, canSpawn, canFit;
    private static Type Model(string name) => Assembly.Load(new AssemblyName("Jade.GameModel")).GetType("Jade.GameModel." + name, true);
    private static object Get(object obj, string name) => DebugWorkspace.ReadMember(obj, name);
    private static object Call(object obj, string name) => DebugWorkspace.Call(obj, name);
    private static MethodInfo Exact(Type type, string name, Type result, params Type[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);
        if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != result)
            throw new InvalidOperationException("Native starting placement binding missing: " + name);
        return method;
    }

    private StartingFormRelocation(object board, object piece, UObject definition)
    {
        this.board = board;
        tiles = Get(board, "tileSet");
        pieceId = Get(piece, "pieceId");
        footprint = HeroForms.FormFootprint(definition, (float)Get(piece, "orientation"));
        var point = Model("IntPoint2D"); var rect = Model("TileRect");
        var tileSet = Model("TileSet"); var tile = Model("Tile");
        var tileHelpers = Model("TileSetHelpers"); var boardHelpers = Model("BoardHelpers");
        var boardType = Model("Board"); var idType = Model("PieceId");
        pointCtor = point.GetConstructor(new[] { typeof(int), typeof(int) });
        rectCtor = rect.GetConstructor(new[] { point, point });
        if (pointCtor == null || rectCtor == null || !tileSet.IsInstanceOfType(tiles))
            throw new InvalidOperationException("Native starting placement geometry unavailable.");
        exists = Exact(tileHelpers, "TileExist", typeof(bool), tileSet, typeof(int), typeof(int));
        tileAt = Exact(tileHelpers, "GetTile", tile, tileSet, typeof(int), typeof(int));
        wall = Exact(tileHelpers, "WallInTheWayOfMove", typeof(bool), tileSet, point, point);
        occupied = Exact(boardHelpers, "TryGetPieceAtPoint", typeof(bool), boardType, point, Model("Piece").MakeByRefType());
        walkable = Exact(boardHelpers, "IsWalkable", typeof(bool), boardType, point);
        canSpawn = Exact(boardHelpers, "CanSpawnPieceOnPosition", typeof(bool), boardType, point, idType);
        canFit = Exact(boardHelpers, "CanSpawnPieceInArea", typeof(bool), boardType, rect, idType);
    }
    private object Point(int x, int y) => pointCtor.Invoke(new object[] { x, y });
    private bool Exists(int x, int y) => exists.Invoke(null, new[] { tiles, (object)x, y }) is bool yes && yes;
    public bool CanStep(int fromX, int fromY, int toX, int toY)
    {
        if (!Exists(fromX, fromY) || !Exists(toX, toY)) return false;
        var from = Point(fromX, fromY); var to = Point(toX, toY);
        if (wall.Invoke(null, new[] { tiles, from, to }) is not bool blockedByWall || blockedByWall) return false;
        var tile = tileAt.Invoke(null, new[] { tiles, (object)toX, toY });
        return Call(tile, "get_IsWalkable") is bool tileWalkable && tileWalkable &&
            Get(tile, "tileIsTemporarilyBlocked") is bool blocked && !blocked &&
            occupied.Invoke(null, new[] { board, to, (object)null }) is bool hasPiece && !hasPiece &&
            walkable.Invoke(null, new[] { board, to }) is bool canWalk && canWalk &&
            canSpawn.Invoke(null, new[] { board, to, (object)null }) is bool valid && valid;
    }
    public bool FitsForm(int x, int y)
    {
        if (!Exists(x, y)) return false;
        var area = rectCtor.Invoke(new[] { Point(x, y), footprint });
        return canFit.Invoke(null, new[] { board, area, pieceId }) is bool fits && fits;
    }
    internal static bool TryFind(object board, object piece, UObject definition, out int destX, out int destY)
    {
        var position = Get(piece, "gridPos");
        int x = (int)Get(position, "x"), y = (int)Get(position, "y");
        destX = x; destY = y;
        var center = Call(piece, "get_Center");
        if ((int)Get(center, "x") != x || (int)Get(center, "y") != y)
            throw new InvalidOperationException("Starting hero center differs from its grid position.");
        return StartingFormPlacement.TryFind(new StartingFormRelocation(board, piece, definition), x, y, out destX, out destY);
    }
}
