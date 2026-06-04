using System;
using System.Collections.Generic;
using System.Linq;
using Engine;
using Engine.Content;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace CircuitAutoRouter
{
    // Mod states
    public enum RouterMode
    {
        None,       // Idle
        SetArea1,   // Confirm selection as Area 1
        SetArea2,   // Confirm selection as Area 2
        SetNumber,  // Click WireThrough blocks to assign numbers
        ChainLink   // Manual chain-link mode: press numpad to extend path
    }

    public class SubsystemCircuitRouter : SubsystemBlockBehavior, IDrawable, IUpdateable
    {
        // Rod BlockIndex
        // Source: RodBlock.cs
        public const int RodBlockIndex = 195;

        public override int[] HandledBlocks => new int[] { RodBlockIndex };

        // IDrawable
        private static int[] m_drawOrders = new int[] { 100 };
        public int[] DrawOrders => m_drawOrders;

        // IUpdateable
        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        // Subsystem refs
        private SubsystemTerrain m_subsystemTerrain;
        private SubsystemGameWidgets m_subsystemGameWidgets;

        // State
        private RouterMode m_mode = RouterMode.None;
        private CircuitRouterWidget m_routerWidget;
        private ComponentPlayer m_componentPlayer;

        // Chain-link mode: manual step-by-step block placement
        // Records the sequence of basic blocks built by the user via numpad
        private List<Point3> m_chainBlocks = new List<Point3>();
        // Records the direction each block was placed (for logging/debugging)
        private List<int> m_chainDirections = new List<int>(); // face index of direction
        public bool IsChainLinkMode => m_mode == RouterMode.ChainLink;
        public int ChainBlockCount => m_chainBlocks.Count;

        // Current selection (green wireframe, two-point rectangle mode)
        private Point3? m_selectionPoint1;
        private Point3? m_selectionPoint2;
        private BoundingBox? m_selectedBox;
        private List<Point3> m_selectedBlocks = new List<Point3>();

        // Confirmed Area 1 (yellow) & Area 2 (red)
        private List<Point3> m_area1Blocks = new List<Point3>();
        private BoundingBox? m_area1Box;
        private int m_area1ActiveFace = -1; // -1=none, 0=+Z, 1=+X, 2=-Z, 3=-X, 4=+Y, 5=-Y
        private List<Point3> m_area2Blocks = new List<Point3>();
        private BoundingBox? m_area2Box;
        private int m_area2ActiveFace = -1;

        // Rendering
        private PrimitivesRenderer3D m_primitivesRenderer3D = new PrimitivesRenderer3D();

        // Circuit number for the current connection
        private int m_circuitNumber = 1;

        // SetNumber mode: per-block numbering for WireThrough blocks
        // Key = block position, Value = assigned number
        private Dictionary<Point3, int> m_area1Numbers = new Dictionary<Point3, int>();
        private Dictionary<Point3, int> m_area2Numbers = new Dictionary<Point3, int>();
        private int m_area1NextNumber = 1;
        private int m_area2NextNumber = 1;
        public bool IsSetNumberMode => m_mode == RouterMode.SetNumber;

        public override bool OnUse(Ray3 ray, ComponentMiner componentMiner)
        {
            if (componentMiner.ComponentPlayer == null) return false;

            m_componentPlayer = componentMiner.ComponentPlayer;

            TerrainRaycastResult? raycastResult = componentMiner.Raycast<TerrainRaycastResult>(ray, RaycastMode.Digging);
            if (raycastResult.HasValue)
            {
                Point3 hit = raycastResult.Value.CellFace.Point;

                // SetNumber mode: click WireThrough blocks in areas to assign numbers
                if (m_mode == RouterMode.SetNumber)
                {
                    HandleSetNumberClick(hit);
                    return true;
                }

                // Normal mode: Rod clicks toggle between point1 and point2
                if (!m_selectionPoint1.HasValue)
                {
                    // First click: set point 1
                    m_selectionPoint1 = hit;
                    m_selectionPoint2 = null;
                    m_selectedBox = new BoundingBox(
                        new Vector3(hit.X, hit.Y, hit.Z),
                        new Vector3(hit.X + 1, hit.Y + 1, hit.Z + 1));
                    m_selectedBlocks.Clear();
                    m_selectedBlocks.Add(hit);
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"Point 1: ({hit.X},{hit.Y},{hit.Z})", Color.Green, false, false);
                }
                else if (!m_selectionPoint2.HasValue)
                {
                    // Second click: set point 2, compute rectangle
                    m_selectionPoint2 = hit;
                    ComputeSelectionBox();
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"Point 2: ({hit.X},{hit.Y},{hit.Z}) [{m_selectedBlocks.Count} blocks]", Color.Green, false, false);
                }
                else
                {
                    // Third click: keep point2 as new point1, set new point2
                    m_selectionPoint1 = m_selectionPoint2;
                    m_selectionPoint2 = hit;
                    ComputeSelectionBox();
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"Point 2: ({hit.X},{hit.Y},{hit.Z}) [{m_selectedBlocks.Count} blocks]", Color.Green, false, false);
                }

                return true;
            }

            return false;
        }

        // Source: CreatorWandMod �?OnEditInventoryItem opens UI when player taps Rod in inventory
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex, ComponentPlayer componentPlayer)
        {
            m_componentPlayer = componentPlayer;

            // Toggle router widget
            if (m_routerWidget != null && m_componentPlayer.ComponentGui.ModalPanelWidget == m_routerWidget)
            {
                // Don't reset SetNumber mode when closing panel via G key
                if (m_mode != RouterMode.SetNumber)
                    m_mode = RouterMode.None;
                Log.Information($"[CircuitAutoRouter] Close panel: mode after={m_mode}");
                m_componentPlayer.ComponentGui.ModalPanelWidget = null;
                m_routerWidget = null;
            }
            else
            {
                Log.Information($"[CircuitAutoRouter] Open panel: current mode={m_mode}, IsSetNumber={IsSetNumberMode}");
                m_routerWidget = new CircuitRouterWidget(this, m_componentPlayer);
                m_componentPlayer.ComponentGui.ModalPanelWidget = m_routerWidget;
            }
            return true;
        }

        void IUpdateable.Update(float dt)
        {
        }

        public void Draw(Camera camera, int drawOrder)
        {
            FlatBatch3D flatBatch = m_primitivesRenderer3D.FlatBatch();

            // Draw current selection (green wireframe)
            if (m_selectedBox.HasValue)
            {
                DrawWireframeBox(flatBatch, m_selectedBox.Value, new Color(0, 255, 0, 180));
            }

            // Draw confirmed Area 1 (yellow wireframe + active face per-block)
            if (m_area1Box.HasValue)
            {
                DrawWireframeBox(flatBatch, m_area1Box.Value, new Color(255, 255, 0, 200));
                if (m_area1ActiveFace >= 0)
                    DrawFacePerBlock(flatBatch, m_area1Box.Value, m_area1ActiveFace,
                        new Color(0, 255, 0, 5), new Color(255, 255, 0, 5));
            }

            // Draw confirmed Area 2 (red wireframe + active face per-block)
            if (m_area2Box.HasValue)
            {
                DrawWireframeBox(flatBatch, m_area2Box.Value, new Color(255, 60, 60, 200));
                if (m_area2ActiveFace >= 0)
                    DrawFacePerBlock(flatBatch, m_area2Box.Value, m_area2ActiveFace,
                        new Color(0, 255, 0, 5), new Color(255, 60, 60, 5));
            }

            // Draw numbers on WireThrough blocks
            FontBatch3D fontBatch = m_primitivesRenderer3D.FontBatch(ContentManager.Get<BitmapFont>("Fonts/Pericles18"), 0, DepthStencilState.None);
            DrawNumbers(fontBatch, m_area1Numbers, m_area1ActiveFace, Color.Yellow);
            DrawNumbers(fontBatch, m_area2Numbers, m_area2ActiveFace, new Color(255, 100, 100));

            // Draw path wireframes with circuit colors
            for (int pi = 0; pi < m_paths.Count; pi++)
            {
                Color pathColor = m_pathColors[pi];
                Color wireColor = new Color((byte)pathColor.R, (byte)pathColor.G, (byte)pathColor.B, (byte)200);
                foreach (var p in m_paths[pi])
                {
                    var box = new BoundingBox(
                        new Vector3(p.X, p.Y, p.Z),
                        new Vector3(p.X + 1, p.Y + 1, p.Z + 1));
                    DrawWireframeBox(flatBatch, box, wireColor);
                }
            }

            // Draw interface faces (semi-transparent red, alpha=0.3)
            for (int ii = 0; ii < m_interfaceCells.Count; ii++)
            {
                Point3 p = m_interfaceCells[ii];
                int face = m_interfaceFaces[ii];
                DrawInterfaceFace(flatBatch, p, face);
            }

            m_primitivesRenderer3D.Flush(camera.ViewProjectionMatrix);
        }

        // --- Public API for Widget ---

        public RouterMode Mode => m_mode;

        public void SetMode(RouterMode mode)
        {
            if (mode == RouterMode.SetArea1)
            {
                if (m_selectedBlocks.Count == 0)
                {
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        "Select blocks with Rod first!", Color.Red, true, false);
                    return;
                }
                m_area1Blocks = new List<Point3>(m_selectedBlocks);
                m_area1Box = m_selectedBox;
                m_area1Numbers.Clear();
                m_area1NextNumber = 1;
                ClearSelection();
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    $"Area 1 set: {m_area1Blocks.Count} blocks", Color.Yellow, true, false);
            }
            else if (mode == RouterMode.SetArea2)
            {
                if (m_selectedBlocks.Count == 0)
                {
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        "Select blocks with Rod first!", Color.Red, true, false);
                    return;
                }
                m_area2Blocks = new List<Point3>(m_selectedBlocks);
                m_area2Box = m_selectedBox;
                m_area2Numbers.Clear();
                m_area2NextNumber = 1;
                ClearSelection();
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    $"Area 2 set: {m_area2Blocks.Count} blocks", new Color(255, 60, 60), true, false);
            }
            m_mode = mode;
        }

        // Toggle SetNumber mode on/off
        public void ToggleSetNumber()
        {
            if (m_mode == RouterMode.SetNumber)
            {
                m_mode = RouterMode.None;
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Set Number OFF", Color.White, false, false);
            }
            else
            {
                // Clear old numbering data when entering SetNumber mode
                m_area1Numbers.Clear();
                m_area2Numbers.Clear();
                m_area1NextNumber = 1;
                m_area2NextNumber = 1;
                m_mode = RouterMode.SetNumber;
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Set Number ON �?click WireThrough blocks", Color.Green, false, false);
            }
        }

        // Handle click in SetNumber mode
        private void HandleSetNumberClick(Point3 hit)
        {
            // Check if hit is in Area1 or Area2
            bool inArea1 = m_area1Blocks.Contains(hit);
            bool inArea2 = m_area2Blocks.Contains(hit);

            if (!inArea1 && !inArea2)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Click inside Area 1 or Area 2!", Color.Red, true, false);
                return;
            }

            // Check if it's a WireThrough block with wire on the active face
            int cellValue = m_subsystemTerrain.Terrain.GetCellValue(hit.X, hit.Y, hit.Z);
            int contents = Terrain.ExtractContents(cellValue);
            if (!WireThroughIndices.Contains(contents))
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Not a WireThrough block!", Color.Red, true, false);
                return;
            }

            int data = Terrain.ExtractData(cellValue);
            int wiredFace = WireThroughBlock.GetWiredFace(data);
            int activeFace = inArea1 ? m_area1ActiveFace : m_area2ActiveFace;
            if (activeFace < 0)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Set active face first!", Color.Red, true, false);
                return;
            }

            // Check if active face matches wired face or its opposite
            if (activeFace != wiredFace && activeFace != CellFace.OppositeFace(wiredFace))
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    $"Wire direction ({FaceNames[wiredFace]}) != active face ({FaceNames[activeFace]})!", Color.Red, true, false);
                return;
            }

            // Assign number
            if (inArea1)
            {
                if (!m_area1Numbers.ContainsKey(hit))
                {
                    m_area1Numbers[hit] = m_area1NextNumber++;
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"Area1 [{hit.X},{hit.Y},{hit.Z}] = {m_area1Numbers[hit]}", Color.Yellow, false, false);
                }
                else
                {
                    // Already numbered, increment
                    m_area1Numbers[hit]++;
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"Area1 [{hit.X},{hit.Y},{hit.Z}] = {m_area1Numbers[hit]}", Color.Yellow, false, false);
                }
            }
            else // inArea2
            {
                if (!m_area2Numbers.ContainsKey(hit))
                {
                    m_area2Numbers[hit] = m_area2NextNumber++;
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"Area2 [{hit.X},{hit.Y},{hit.Z}] = {m_area2Numbers[hit]}", new Color(255, 100, 100), false, false);
                }
                else
                {
                    m_area2Numbers[hit]++;
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"Area2 [{hit.X},{hit.Y},{hit.Z}] = {m_area2Numbers[hit]}", new Color(255, 100, 100), false, false);
                }
            }
        }

        // Path data for visualization
        // Path data for visualization
        private List<List<Point3>> m_paths = new List<List<Point3>>();
        private List<Color> m_pathColors = new List<Color>();
        private List<Point3> m_interfaceCells = new List<Point3>();
        private List<int> m_interfaceFaces = new List<int>();
        // Occupied cells: used by already-placed paths, blocked for subsequent paths
        private HashSet<Point3> m_occupiedCells = new HashSet<Point3>();
        // Protected approach cells: must NOT be freed by ProtectNumberedCells
        private HashSet<Point3> m_protectedCells = new HashSet<Point3>();
        // Multi-number cells: blocks that have a number (in either area) that needs to connect
        // These must not be completely surrounded by occupied cells
        private HashSet<Point3> m_singleEndCells = new HashSet<Point3>();
        private HashSet<Point3> m_numberedCells = new HashSet<Point3>();

        // Color palette: 1=white, 2=cyan, 3=red, 4=blue, 5=yellow, 6=green, 7=orange, 8=purple, cycle
        private static readonly Color[] CircuitColors = new Color[]
        {
            Color.White,        // 1
            Color.Cyan,         // 2
            Color.Red,          // 3
            Color.Blue,         // 4
            Color.Yellow,       // 5
            Color.Green,        // 6
            new Color(255, 165, 0), // 7 orange
            new Color(160, 32, 240) // 8 purple
        };

        private Color GetCircuitColor(int num)
        {
            return CircuitColors[((num - 1) % CircuitColors.Length)];
        }

        // Zigzag connection order: n/2, +1, -2, +3, -4...
        // n=8: 4,5,3,6,2,7,1,8  n=7: 4,5,3,6,2,7,1
        private List<int> GetConnectionOrder(int count)
        {
            var order = new List<int>();
            int current = count / 2 + 1;
            order.Add(current);
            int offset = 1;
            while (order.Count < count)
            {
                int next = current + (offset % 2 == 1 ? offset : -offset);
                if (next >= 1 && next <= count && !order.Contains(next))
                    order.Add(next);
                offset++;
                if (offset > count * 2) break;
            }
            for (int i = 1; i <= count; i++)
                if (!order.Contains(i)) order.Add(i);
            return order;
        }

        // Get preferred direction from start area's active face
        // activeFace indicates the face of the WireThrough block where wire connects,
        // the actual movement direction is the opposite (moving away from that face)
        private Point3 GetPreferredDirection(bool fromArea1)
        {
            int activeFace = fromArea1 ? m_area1ActiveFace : m_area2ActiveFace;
            return FaceDirections[CellFace.OppositeFace(activeFace)];
        }

        public void Connect()
        {
            if (!m_area1Box.HasValue || !m_area2Box.HasValue)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Set both areas first!", Color.Red, true, false);
                return;
            }
            if (m_area1ActiveFace < 0 || m_area2ActiveFace < 0)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "Set active faces first!", Color.Red, true, false);
                return;
            }

            // Collect numbers
            var area1Nums = new HashSet<int>();
            foreach (var a1 in m_area1Numbers) area1Nums.Add(a1.Value);
            var area2Nums = new HashSet<int>();
            foreach (var a2 in m_area2Numbers) area2Nums.Add(a2.Value);

            // Connectable numbers exist in both areas
            var connectableNums = new HashSet<int>();
            foreach (var n in area1Nums)
                if (area2Nums.Contains(n)) connectableNums.Add(n);

            // Single-end cells: only in one area, must not be blocked
            m_singleEndCells.Clear();
            m_numberedCells.Clear();
            foreach (var a1 in m_area1Numbers)
            {
                m_numberedCells.Add(a1.Key);
                if (!area2Nums.Contains(a1.Value)) m_singleEndCells.Add(a1.Key);
            }
            foreach (var a2 in m_area2Numbers)
            {
                m_numberedCells.Add(a2.Key);
                if (!area1Nums.Contains(a2.Value)) m_singleEndCells.Add(a2.Key);
            }

            var sortedNumbers = new List<int>(connectableNums);
            sortedNumbers.Sort();

            if (sortedNumbers.Count == 0)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "No matching numbers found!", Color.Red, true, false);
                return;
            }

            var order = GetConnectionOrder(sortedNumbers.Count);
            Log.Information($"[CircuitAutoRouter] Order: {string.Join(",", order)}, single-end: {m_singleEndCells.Count}");

            m_paths.Clear();
            m_pathColors.Clear();
            m_interfaceCells.Clear();
            m_interfaceFaces.Clear();
            m_occupiedCells.Clear();
            m_protectedCells.Clear();

            // Area blocks are occupied (paths can start/end there but not cross)
            foreach (var p in m_area1Blocks) m_occupiedCells.Add(p);
            foreach (var p in m_area2Blocks) m_occupiedCells.Add(p);
            foreach (var p in m_singleEndCells) m_occupiedCells.Add(p);

            int firstNum = order[0];
            int successCount = 0;

            for (int oi = 0; oi < order.Count; oi++)
            {
                int num = order[oi];

                Point3? area1Point = null, area2Point = null;
                foreach (var a1 in m_area1Numbers)
                    if (a1.Value == num) { area1Point = a1.Key; break; }
                foreach (var a2 in m_area2Numbers)
                    if (a2.Value == num) { area2Point = a2.Key; break; }

                if (!area1Point.HasValue || !area2Point.HasValue)
                {
                    Log.Information($"[CircuitAutoRouter] Skip #{num}: not in both areas");
                    continue;
                }

                // Alternate: even �?from Area1, odd �?from Area2
                bool fromArea1 = (oi % 2 == 0);
                Point3 start, end;
                if (fromArea1) { start = area1Point.Value; end = area2Point.Value; }
                else { start = area2Point.Value; end = area1Point.Value; }

                Point3 preferredDir = GetPreferredDirection(fromArea1);
                // Banned first direction: opposite of active face (can't go backward on first step from start or end)
                Point3 bannedStartDir = new Point3(-preferredDir.X, -preferredDir.Y, -preferredDir.Z);
                // For end point: it belongs to the other area, so use that area's active face
                Point3 endPreferredDir = GetPreferredDirection(!fromArea1);
                Point3 bannedEndDir = new Point3(-endPreferredDir.X, -endPreferredDir.Y, -endPreferredDir.Z); // Ban approaching end from its front side (active face direction)

                Log.Information($"[CircuitAutoRouter] Connect #{num}: ({start.X},{start.Y},{start.Z})->({end.X},{end.Y},{end.Z}) from={(fromArea1 ? "A1" : "A2")} pref={preferredDir.X},{preferredDir.Y},{preferredDir.Z}");

                List<Point3> path = FindPath(start, end, preferredDir, bannedStartDir, bannedEndDir, m_occupiedCells);
                if (path == null || path.Count == 0)
                {
                    Log.Information($"[CircuitAutoRouter] No path for #{num}");
                    m_componentPlayer.ComponentGui.DisplaySmallMessage(
                        $"No path for #{num}!", Color.Red, false, false);
                    continue;
                }

                // Add ALL path cells (including start/end) to occupied set
                for (int i = 0; i < path.Count; i++)
                    m_occupiedCells.Add(path[i]);

                // Protect approach cells: start+preferredDir and end-endPreferredDir
                // Start: path must go through start+preferredDir on first step
                // End: path arrives from end-endPreferredDir side (bannedEndDir=-endPreferredDir bans dirs[d]=-endPreferredDir, so path enters from end-endPreferredDir side via +endPreferredDir)
                Point3 startApproach = new Point3(start.X + preferredDir.X, start.Y + preferredDir.Y, start.Z + preferredDir.Z);
                Point3 endApproach = new Point3(end.X - endPreferredDir.X, end.Y - endPreferredDir.Y, end.Z - endPreferredDir.Z);
                if (!m_area1Blocks.Contains(startApproach) && !m_area2Blocks.Contains(startApproach))
                {
                    m_occupiedCells.Add(startApproach);
                    m_protectedCells.Add(startApproach);
                    Log.Information($"[CircuitAutoRouter] Protected start approach ({startApproach.X},{startApproach.Y},{startApproach.Z})");
                }
                if (!m_area1Blocks.Contains(endApproach) && !m_area2Blocks.Contains(endApproach))
                {
                    m_occupiedCells.Add(endApproach);
                    m_protectedCells.Add(endApproach);
                    Log.Information($"[CircuitAutoRouter] Protected end approach ({endApproach.X},{endApproach.Y},{endApproach.Z})");
                }

                // Protect numbered cells from being surrounded
                ProtectNumberedCells();

                // Interface calculation
                int diff = num - firstNum;
                if (diff != 0 && path.Count > 4)
                {
                    int shift = diff > 0 ? 2 : -2;
                    int boundaryIdx = path.Count / 2 + shift;
                    if (boundaryIdx < 1) boundaryIdx = 1;
                    if (boundaryIdx >= path.Count - 1) boundaryIdx = path.Count - 2;

                    Point3 bc = path[boundaryIdx];
                    Point3 nc = path[boundaryIdx + 1];
                    Point3 dir = new Point3(nc.X - bc.X, nc.Y - bc.Y, nc.Z - bc.Z);
                    int iface = DirectionToFace(dir);
                    m_interfaceCells.Add(bc);
                    m_interfaceFaces.Add(iface);
                    Log.Information($"[CircuitAutoRouter] Interface at ({bc.X},{bc.Y},{bc.Z}) face={FaceNames[iface]} diff={diff}");
                }
                else if (diff == 0 && path.Count > 2)
                {
                    int mid = path.Count / 2;
                    Point3 bc = path[mid];
                    Point3 nc = path[mid + 1];
                    Point3 dir = new Point3(nc.X - bc.X, nc.Y - bc.Y, nc.Z - bc.Z);
                    int iface = DirectionToFace(dir);
                    m_interfaceCells.Add(bc);
                    m_interfaceFaces.Add(iface);
                    Log.Information($"[CircuitAutoRouter] Interface(base) at ({bc.X},{bc.Y},{bc.Z}) face={FaceNames[iface]}");
                }

                Log.Information($"[CircuitAutoRouter] Path #{num}: {path.Count} pts");
                m_paths.Add(path);
                m_pathColors.Add(GetCircuitColor(num));

                // 3D Wiring: determine block types and place in terrain
                int startActiveFace = fromArea1 ? m_area1ActiveFace : m_area2ActiveFace;
                int endActiveFace = fromArea1 ? m_area2ActiveFace : m_area1ActiveFace;
                List<RoutingCell> routingCells = DetermineRouting(path, startActiveFace, endActiveFace);
                PlaceWires(routingCells);
                Log.Information($"[CircuitAutoRouter] Placed #{num}: {routingCells.Count} routing cells");

                successCount++;
            }

            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Found & placed {successCount}/{sortedNumbers.Count} paths",
                Color.Green, true, false);
        }

        // ============================================================
        // Chain-link mode: Manual step-by-step block placement
        // ============================================================

        /// Start chain-link mode from Area1's number 1 block
        public void StartChainLink()
        {
            if (m_area1Numbers.Count == 0)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    "No Area1 blocks set! Set numbers first.", Color.Red, true, false);
                return;
            }

            // Find the block with number 1 in Area1
            var sortedArea1Numbers = m_area1Numbers.Values.OrderBy(n => n).ToList();
            int num = sortedArea1Numbers.Count > 0 ? sortedArea1Numbers[0] : 1;
            Point3? startPos = FindNumberedBlock(num, isArea1: true);
            if (startPos == null)
            {
                m_componentPlayer.ComponentGui.DisplaySmallMessage(
                    $"Block #{num} not found in Area1!", Color.Red, true, false);
                return;
            }

            m_chainBlocks.Clear();
            m_chainDirections.Clear();
            m_chainBlocks.Add(startPos.Value);
            m_chainDirections.Add(-1); // start block, no direction

            m_mode = RouterMode.ChainLink;

            // Generate initial block (just the start WireThrough)
            RegenerateChainLink();

            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Chain-link: Start at ({startPos.Value.X},{startPos.Value.Y},{startPos.Value.Z}), use Numpad to extend",
                Color.Yellow, true, false);
            Log.Information($"[CircuitAutoRouter] Chain-link started at ({startPos.Value.X},{startPos.Value.Y},{startPos.Value.Z})");
        }

        // Process numpad key press in chain-link mode
        // Returns true if a key was handled
        public bool HandleChainLinkKey(int key)
        {
            if (m_mode != RouterMode.ChainLink) return false;

            // Map numpad keys to face directions
            // 2=-Z(Face2), 4=-X(Face3), 6=+X(Face1), 8=+Z(Face0), 7=+Y(Face4), 9=-Y(Face5)
            int face = -1;
            switch (key)
            {
                case 86: face = 2; break; // Numpad2 → -Z (Face 2, 上)
                case 88: face = 3; break; // Numpad4 → -X (Face 3, 左)
                case 90: face = 1; break; // Numpad6 → +X (Face 1, 右)
                case 92: face = 0; break; // Numpad8 → +Z (Face 0, 下)
                case 91: face = 4; break; // Numpad7 → +Y (Face 4, 上方)
                case 93: face = 5; break; // Numpad9 → -Y (Face 5, 下方)
                default: return false;
            }

            // Calculate new position from last block + direction
            Point3 lastPos = m_chainBlocks[m_chainBlocks.Count - 1];
            Point3 dir = FaceDirections[face];
            Point3 newPos = new Point3(lastPos.X + dir.X, lastPos.Y + dir.Y, lastPos.Z + dir.Z);

            m_chainBlocks.Add(newPos);
            m_chainDirections.Add(face);

            // Regenerate all blocks from scratch
            RegenerateChainLink();

            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Chain #{m_chainBlocks.Count}: ({newPos.X},{newPos.Y},{newPos.Z}) dir=Face{face}",
                Color.Yellow, true, false);

            return true;
        }

        // Undo last chain-link step
        public void UndoChainLink()
        {
            if (m_mode != RouterMode.ChainLink || m_chainBlocks.Count <= 1) return;

            m_chainBlocks.RemoveAt(m_chainBlocks.Count - 1);
            m_chainDirections.RemoveAt(m_chainDirections.Count - 1);

            RegenerateChainLink();

            Point3 lastPos = m_chainBlocks[m_chainBlocks.Count - 1];
            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Chain undo: {m_chainBlocks.Count} blocks, last=({lastPos.X},{lastPos.Y},{lastPos.Z})",
                Color.Yellow, true, false);
        }

        // Exit chain-link mode
        public void ExitChainLink()
        {
            m_mode = RouterMode.None;
            m_chainBlocks.Clear();
            m_chainDirections.Clear();
            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                "Chain-link mode exited", Color.White, true, false);
        }

        // Regenerate all blocks from the chain-link sequence
        // Clears the affected area first, then places blocks according to routing rules
        private void RegenerateChainLink()
        {
            if (m_chainBlocks.Count == 0) return;

            // Step 1: Clear previously placed blocks from the terrain
            // We track positions we've placed blocks at (all except the start WireThrough)
            ClearChainLinkBlocks();

            // Step 2: Determine routing for the current block sequence
            // The start block (index 0) is the existing WireThrough in Area1
            // All subsequent blocks are manually placed
            // InFace of start = activeFace, OutFace of last = activeFace (or -1)
            int startInFace = m_area1ActiveFace;
            int endOutFace = -1; // no fixed end direction

            List<RoutingCell> routingCells = DetermineChainRouting(m_chainBlocks, m_chainDirections, startInFace, endOutFace);
            PlaceChainLinkBlocks(routingCells);

            // Step 3: Log the current state
            LogChainLinkState(routingCells);
        }

        // Track positions where we placed blocks for cleanup
        private HashSet<Point3> m_chainPlacedPositions = new HashSet<Point3>();

        private void ClearChainLinkBlocks()
        {
            foreach (Point3 pos in m_chainPlacedPositions)
            {
                // Only clear if it's still a wire/wire-through block we placed
                int cellValue = m_subsystemTerrain.Terrain.GetCellValue(pos.X, pos.Y, pos.Z);
                int contents = Terrain.ExtractContents(cellValue);
                if (contents == WireBlock.Index || WireThroughIndices.Contains(contents))
                {
                    m_subsystemTerrain.ChangeCell(pos.X, pos.Y, pos.Z, 0); // set to air
                }
            }
            m_chainPlacedPositions.Clear();
        }

        // Determine routing for chain-link blocks
        // Similar to DetermineRouting but uses manually defined directions instead of auto-calculated faces
        private List<RoutingCell> DetermineChainRouting(List<Point3> blocks, List<int> directions, int startInFace, int endOutFace)
        {
            var cells = new List<RoutingCell>();
            if (blocks.Count == 0) return cells;

            for (int i = 0; i < blocks.Count; i++)
            {
                var cell = new RoutingCell
                {
                    Position = blocks[i],
                    IsSupplement = false,
                    InFace = -1,
                    OutFace = -1,
                    WireFacesBitmask = 0,
                    WireThroughWiredFace = 0,
                    WireThroughContentIndex = DefaultWireThroughContentIndex
                };

                // InFace: direction from previous block (or startFace for first block)
                if (i == 0)
                {
                    cell.InFace = startInFace;
                }
                else
                {
                    // The face that points toward the previous block
                    cell.InFace = CellFace.OppositeFace(directions[i]);
                }

                // OutFace: direction toward next block (or -1 for last block)
                if (i == blocks.Count - 1)
                {
                    cell.OutFace = endOutFace >= 0 ? endOutFace : -1;
                }
                else
                {
                    // The direction the user pressed for this step = the face pointing toward next
                    cell.OutFace = directions[i + 1];
                }

                // If OutFace is -1 (end block, no next), just use InFace
                if (cell.OutFace == -1)
                {
                    // End block: just mark as WireThrough in the in-face direction
                    cell.IsWireThrough = true;
                    cell.WireThroughWiredFace = FaceToWiredFace(cell.InFace);
                    cell.WireThroughContentIndex = DetectWireThroughContentType(blocks[i]);
                }
                else if (AreOppositeFaces(cell.InFace, cell.OutFace))
                {
                    // Opposite faces → WireThrough
                    cell.IsWireThrough = true;
                    cell.WireThroughWiredFace = FaceToWiredFace(cell.OutFace);
                    cell.WireThroughContentIndex = DetectWireThroughContentType(blocks[i]);
                }
                else
                {
                    // Non-opposite (turn) → WireBlock
                    cell.IsWireThrough = false;
                    cell.WireFacesBitmask = (1 << cell.InFace) | (1 << cell.OutFace);
                }

                cells.Add(cell);
            }

            // Process Z interference
            ProcessZInterference(cells, blocks);

            return cells;
        }

        // Place chain-link blocks and track positions for cleanup
        private void PlaceChainLinkBlocks(List<RoutingCell> routingCells)
        {
            // First pass: collect all positions
            foreach (var cell in routingCells)
            {
                // Skip the first block (Area1's existing WireThrough)
                if (m_chainBlocks.Count > 0 && cell.Position.Equals(m_chainBlocks[0]) && !cell.IsSupplement)
                    continue;
                m_chainPlacedPositions.Add(cell.Position);
            }

            // Second pass: place using the same logic as PlaceWires
            var cellMap = new Dictionary<Point3, RoutingCell>();
            foreach (var cell in routingCells)
            {
                // Skip the first block (Area1's existing WireThrough)
                if (m_chainBlocks.Count > 0 && cell.Position.Equals(m_chainBlocks[0]) && !cell.IsSupplement)
                    continue;

                if (cellMap.TryGetValue(cell.Position, out RoutingCell existing))
                {
                    if (!existing.IsWireThrough && !cell.IsWireThrough)
                    {
                        existing.WireFacesBitmask |= cell.WireFacesBitmask;
                    }
                    else if (cell.IsWireThrough)
                    {
                        cellMap[cell.Position] = cell;
                    }
                }
                else
                {
                    cellMap[cell.Position] = cell;
                }
            }

            foreach (var kv in cellMap)
            {
                RoutingCell cell = kv.Value;
                Point3 pos = cell.Position;

                if (cell.IsWireThrough)
                {
                    int data = WireThroughBlock.SetWiredFace(0, cell.WireThroughWiredFace);
                    int value = Terrain.MakeBlockValue(cell.WireThroughContentIndex, 0, data);
                    m_subsystemTerrain.ChangeCell(pos.X, pos.Y, pos.Z, value);
                }
                else
                {
                    int existingValue = m_subsystemTerrain.Terrain.GetCellValue(pos.X, pos.Y, pos.Z);
                    int existingContents = Terrain.ExtractContents(existingValue);
                    int existingBitmask = (existingContents == WireBlock.Index)
                        ? WireBlock.GetWireFacesBitmask(existingValue)
                        : 0;
                    int newBitmask = existingBitmask | cell.WireFacesBitmask;
                    if (newBitmask == 0) continue;
                    int value = WireBlock.SetWireFacesBitmask(
                        Terrain.MakeBlockValue(WireBlock.Index), newBitmask);
                    m_subsystemTerrain.ChangeCell(pos.X, pos.Y, pos.Z, value);
                }
            }
        }

        // Log current chain-link state
        private void LogChainLinkState(List<RoutingCell> routingCells)
        {
            Log.Information($"[CircuitAutoRouter] Chain-link: {m_chainBlocks.Count} blocks, {routingCells.Count} routing cells");
            for (int i = 0; i < routingCells.Count; i++)
            {
                var c = routingCells[i];
                string type = c.IsWireThrough ? $"WireThrough(wiredFace={c.WireThroughWiredFace})" : $"WireBlock(faces=0x{c.WireFacesBitmask:X2})";
                string supplement = c.IsSupplement ? " [SUPPLEMENT]" : "";
                Log.Information($"  [{i}] ({c.Position.X},{c.Position.Y},{c.Position.Z}) {type}{supplement} inF={c.InFace} outF={c.OutFace}");
            }
        }

        // Find a numbered block position
        private Point3? FindNumberedBlock(int number, bool isArea1)
        {
            var dict = isArea1 ? m_area1Numbers : m_area2Numbers;
            foreach (var kv in dict)
            {
                if (kv.Value == number) return kv.Key;
            }
            return null;
        }

        // ============================================================
        // End Chain-link mode
        // ============================================================

        // Ensure numbered cells have at least 1 passable neighbor on the approach side
        // Area1 cells: path departs via activeFace direction, so approach side = +activeFace
        // Area2 cells: path arrives from -activeFace side (bannedEndDir=-activeFace), so approach side = -activeFace
        // IMPORTANT: never free cells in m_protectedCells (approach cells of already-routed paths)
        private void ProtectNumberedCells()
        {
            Point3[] dirs6 = new Point3[]
            {
                new Point3(0, 0, 1), new Point3(1, 0, 0), new Point3(0, 0, -1),
                new Point3(-1, 0, 0), new Point3(0, 1, 0), new Point3(0, -1, 0)
            };
            foreach (Point3 cell in m_numberedCells)
            {
                bool isArea1 = m_area1Numbers.ContainsKey(cell);
                int af = isArea1 ? m_area1ActiveFace : m_area2ActiveFace;
                if (af < 0) continue;

                // Area1: approach = +activeFace (departure direction)
                // Area2: approach = -activeFace (arrival direction: path enters from cell-activeFace side)
                Point3 approachDir = isArea1 ? dirs6[af] : new Point3(-dirs6[af].X, -dirs6[af].Y, -dirs6[af].Z); // Area1: +activeFace, Area2: -activeFace
                Point3 approachNb = new Point3(cell.X + approachDir.X, cell.Y + approachDir.Y, cell.Z + approachDir.Z);
                if (!m_occupiedCells.Contains(approachNb)) continue; // already passable
                if (m_protectedCells.Contains(approachNb)) continue; // don't free protected approach cells

                // Approach neighbor is blocked �?free it
                m_occupiedCells.Remove(approachNb);
                Log.Information($"[CircuitAutoRouter] Protected ({cell.X},{cell.Y},{cell.Z}): freed approach nb ({approachNb.X},{approachNb.Y},{approachNb.Z})");
            }
        }

        // A* pathfinding with direction preference, first-step ban, and occupied cell avoidance
        // preferredDir: direction to prefer for straight-line movement (lower cost)
        // bannedStartDir: direction banned on the first step from start (opposite of start area's active face)
        // bannedEndDir: direction banned on the last step into end (negative of end area's active face)
        // blocked: cells that cannot be used by this path
        private List<Point3> FindPath(Point3 start, Point3 end, Point3 preferredDir, Point3 bannedStartDir, Point3 bannedEndDir, HashSet<Point3> blocked)
        {
            Point3[] dirs = new Point3[]
            {
                new Point3(0, 0, 1),  // +Z face 0
                new Point3(1, 0, 0),  // +X face 1
                new Point3(0, 0, -1), // -Z face 2
                new Point3(-1, 0, 0), // -X face 3
                new Point3(0, 1, 0),  // +Y face 4
                new Point3(0, -1, 0)  // -Y face 5
            };

            if (start.Equals(end))
                return new List<Point3> { start };

            float Heuristic(Point3 a, Point3 b)
            {
                return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) + Math.Abs(a.Z - b.Z);
            }

            var gScore = new Dictionary<Point3, float>();
            var cameFrom = new Dictionary<Point3, Point3>();
            var frontier = new SortedList<float, List<Point3>>();

            void Enqueue(Point3 p, float priority)
            {
                if (!frontier.ContainsKey(priority))
                    frontier[priority] = new List<Point3>();
                frontier[priority].Add(p);
            }

            gScore[start] = 0f;
            Enqueue(start, Heuristic(start, end));
            const int MaxIterations = 100000;
            int iterations = 0;

            while (frontier.Count > 0 && iterations < MaxIterations)
            {
                iterations++;
                var firstKey = frontier.Keys[0];
                var bestList = frontier[firstKey];
                Point3 current = bestList[0];
                bestList.RemoveAt(0);
                if (bestList.Count == 0)
                    frontier.Remove(firstKey);

                if (current.Equals(end))
                {
                    var path = new List<Point3> { current };
                    while (cameFrom.ContainsKey(current))
                    {
                        current = cameFrom[current];
                        path.Add(current);
                    }
                    path.Reverse();
                    return path;
                }

                float curG = gScore[current];

                // Determine the direction we came from (for turn cost)
                Point3 cameFromDir = Point3.Zero;
                if (cameFrom.ContainsKey(current))
                {
                    Point3 prev = cameFrom[current];
                    cameFromDir = new Point3(current.X - prev.X, current.Y - prev.Y, current.Z - prev.Z);
                }

                for (int d = 0; d < 6; d++)
                {
                    Point3 next = new Point3(current.X + dirs[d].X, current.Y + dirs[d].Y, current.Z + dirs[d].Z);
                    
                    // First step from start: ban the direction opposite to start area's active face
                    if (current.Equals(start) && dirs[d].Equals(bannedStartDir))
                        continue;
                    
                    // Last step into end: ban the direction opposite to end area's active face
                    // i.e. don't approach end from its back side
                    if (next.Equals(end) && dirs[d].Equals(bannedEndDir))
                        continue;
                    
                    // Allow start and end even if in area blocks
                    bool isStart = next.Equals(start);
                    bool isEnd = next.Equals(end);
                    
                    if (!IsPassable(next)) continue;
                    
                    // Block occupied cells (unless it's start/end of this path)
                    if (blocked.Contains(next) && !isStart && !isEnd) continue;

                    // Cost: base 1, + turn cost, + preference for preferred direction
                    float moveCost = 1f;

                    // Turn cost: if changing direction from previous step, add cost
                    if (!cameFromDir.Equals(Point3.Zero) && !cameFromDir.Equals(dirs[d]))
                        moveCost += 3f; // Turn penalty

                    // Straight-line preference: reduce cost if moving in preferred direction
                    if (dirs[d].Equals(preferredDir))
                        moveCost -= 0.3f; // Bonus for preferred direction

                    float tentativeG = curG + moveCost;
                    if (!gScore.ContainsKey(next) || tentativeG < gScore[next])
                    {
                        gScore[next] = tentativeG;
                        cameFrom[next] = current;
                        Enqueue(next, tentativeG + Heuristic(next, end));
                    }
                }
            }

            Log.Information($"[CircuitAutoRouter] A* exhausted after {iterations} iterations");
            return null;
        }


        // Dot product for Point3
        private static float Dot(Point3 a, Point3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        // Get a point on the specified face of a BoundingBox
        // Returns the center point of the face, offset one cell outward
        private static Point3 GetBoxFacePoint(BoundingBox box, int face)
        {
            float cx = (box.Min.X + box.Max.X) / 2f;
            float cy = (box.Min.Y + box.Max.Y) / 2f;
            float cz = (box.Min.Z + box.Max.Z) / 2f;

            switch (face)
            {
                case 0: return new Point3((int)cx, (int)cy, (int)box.Max.Z); // +Z
                case 1: return new Point3((int)box.Max.X, (int)cy, (int)cz); // +X
                case 2: return new Point3((int)cx, (int)cy, (int)box.Min.Z - 1); // -Z
                case 3: return new Point3((int)box.Min.X - 1, (int)cy, (int)cz); // -X
                case 4: return new Point3((int)cx, (int)box.Max.Y, (int)cz); // +Y
                case 5: return new Point3((int)cx, (int)box.Min.Y - 1, (int)cz); // -Y
                default: return new Point3((int)cx, (int)cy, (int)cz);
            }
        }

        // Check if a cell is passable for wire routing
        // Passable = air, existing WireThrough block, or existing WireBlock
        private bool IsPassable(Point3 p)
        {
            if (!m_subsystemTerrain.Terrain.IsCellValid(p.X, p.Y, p.Z)) return false;
            int cellValue = m_subsystemTerrain.Terrain.GetCellValue(p.X, p.Y, p.Z);
            int contents = Terrain.ExtractContents(cellValue);
            return contents == 0 || WireThroughIndices.Contains(contents) || contents == WireBlock.Index;
        }

        // Get default WireThrough content type from Area1's first block
        // Direction �?face index
        // Source: CellFace 0=+Z, 1=+X, 2=-Z, 3=-X, 4=+Y, 5=-Y
        private int DirectionToFace(Point3 dir)
        {
            if (dir.Z > 0) return 0;
            if (dir.X > 0) return 1;
            if (dir.Z < 0) return 2;
            if (dir.X < 0) return 3;
            if (dir.Y > 0) return 4;
            return 5; // Y < 0
        }

        public void SetCircuitNumber(int num)
        {
            m_circuitNumber = num;
            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Circuit number set to {m_circuitNumber}",
                Color.White, false, false);
        }

        public int CircuitNumber => m_circuitNumber;
        public int SelectedCount => m_selectedBlocks.Count;
        public int Area1Count => m_area1Blocks.Count;
        public int Area2Count => m_area2Blocks.Count;
        public int Area1ActiveFace => m_area1ActiveFace;
        public int Area2ActiveFace => m_area2ActiveFace;

        // Face names: 0=+Z(South), 1=+X(East), 2=-Z(North), 3=-X(West), 4=+Y(Top), 5=-Y(Bottom)
        private static readonly string[] FaceNames = { "+Z", "+X", "-Z", "-X", "+Y", "-Y" };
        public string Area1FaceName => m_area1ActiveFace >= 0 ? FaceNames[m_area1ActiveFace] : "--";
        public string Area2FaceName => m_area2ActiveFace >= 0 ? FaceNames[m_area2ActiveFace] : "--";

        public void CycleArea1Face()
        {
            if (!m_area1Box.HasValue) return;
            m_area1ActiveFace = (m_area1ActiveFace + 1) % 6;
        }

        public void CycleArea2Face()
        {
            if (!m_area2Box.HasValue) return;
            m_area2ActiveFace = (m_area2ActiveFace + 1) % 6;
        }

        public void ClearSelection()
        {
            m_selectionPoint1 = null;
            m_selectionPoint2 = null;
            m_selectedBlocks.Clear();
            m_selectedBox = null;
        }

        // --- Internal ---

        private void ComputeSelectionBox()
        {
            if (!m_selectionPoint1.HasValue || !m_selectionPoint2.HasValue) return;

            Point3 p1 = m_selectionPoint1.Value;
            Point3 p2 = m_selectionPoint2.Value;

            int minX = Math.Min(p1.X, p2.X);
            int maxX = Math.Max(p1.X, p2.X);
            int minY = Math.Min(p1.Y, p2.Y);
            int maxY = Math.Max(p1.Y, p2.Y);
            int minZ = Math.Min(p1.Z, p2.Z);
            int maxZ = Math.Max(p1.Z, p2.Z);

            m_selectedBox = new BoundingBox(
                new Vector3(minX, minY, minZ),
                new Vector3(maxX + 1, maxY + 1, maxZ + 1));

            m_selectedBlocks.Clear();
            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        m_selectedBlocks.Add(new Point3(x, y, z));
                    }
                }
            }
        }

        private void DrawWireframeBox(FlatBatch3D flatBatch, BoundingBox box, Color color)
        {
            Vector3 min = box.Min;
            Vector3 max = box.Max;

            // Bottom face edges (Y=min.Y)
            flatBatch.QueueLine(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z), color);
            flatBatch.QueueLine(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, min.Y, max.Z), color);
            flatBatch.QueueLine(new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z), color);
            flatBatch.QueueLine(new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, min.Y, min.Z), color);

            // Top face edges (Y=max.Y)
            flatBatch.QueueLine(new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), color);
            flatBatch.QueueLine(new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, max.Y, max.Z), color);
            flatBatch.QueueLine(new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z), color);
            flatBatch.QueueLine(new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, max.Y, min.Z), color);

            // Vertical edges
            flatBatch.QueueLine(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, max.Y, min.Z), color);
            flatBatch.QueueLine(new Vector3(max.X, min.Y, min.Z), new Vector3(max.X, max.Y, min.Z), color);
            flatBatch.QueueLine(new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, max.Y, max.Z), color);
            flatBatch.QueueLine(new Vector3(min.X, min.Y, max.Z), new Vector3(min.X, max.Y, max.Z), color);
        }

        // Face index: 0=+Z, 1=+X, 2=-Z, 3=-X, 4=+Y, 5=-Y
        // Offset quad slightly outward to avoid z-fighting with block surfaces
        private void DrawFaceQuad(FlatBatch3D flatBatch, BoundingBox box, int face, Color color)
        {
            float e = 0.01f; // epsilon offset
            Vector3 min = box.Min;
            Vector3 max = box.Max;

            switch (face)
            {
                case 0: // +Z face (max.Z)
                    flatBatch.QueueQuad(
                        new Vector3(min.X, min.Y, max.Z + e),
                        new Vector3(max.X, min.Y, max.Z + e),
                        new Vector3(max.X, max.Y, max.Z + e),
                        new Vector3(min.X, max.Y, max.Z + e), color);
                    break;
                case 1: // +X face (max.X)
                    flatBatch.QueueQuad(
                        new Vector3(max.X + e, min.Y, min.Z),
                        new Vector3(max.X + e, min.Y, max.Z),
                        new Vector3(max.X + e, max.Y, max.Z),
                        new Vector3(max.X + e, max.Y, min.Z), color);
                    break;
                case 2: // -Z face (min.Z)
                    flatBatch.QueueQuad(
                        new Vector3(max.X, min.Y, min.Z - e),
                        new Vector3(min.X, min.Y, min.Z - e),
                        new Vector3(min.X, max.Y, min.Z - e),
                        new Vector3(max.X, max.Y, min.Z - e), color);
                    break;
                case 3: // -X face (min.X)
                    flatBatch.QueueQuad(
                        new Vector3(min.X - e, min.Y, max.Z),
                        new Vector3(min.X - e, min.Y, min.Z),
                        new Vector3(min.X - e, max.Y, min.Z),
                        new Vector3(min.X - e, max.Y, max.Z), color);
                    break;
                case 4: // +Y face (max.Y)
                    flatBatch.QueueQuad(
                        new Vector3(min.X, max.Y + e, min.Z),
                        new Vector3(max.X, max.Y + e, min.Z),
                        new Vector3(max.X, max.Y + e, max.Z),
                        new Vector3(min.X, max.Y + e, max.Z), color);
                    break;
                case 5: // -Y face (min.Y)
                    flatBatch.QueueQuad(
                        new Vector3(min.X, min.Y - e, max.Z),
                        new Vector3(max.X, min.Y - e, max.Z),
                        new Vector3(max.X, min.Y - e, min.Z),
                        new Vector3(min.X, min.Y - e, min.Z), color);
                    break;
            }
        }

        // Draw numbers on WireThrough blocks' active face
        private void DrawNumbers(FontBatch3D fontBatch, Dictionary<Point3, int> numbers, int activeFace, Color color)
        {
            if (activeFace < 0 || numbers.Count == 0) return;

            foreach (var kv in numbers)
            {
                Point3 p = kv.Key;
                string text = kv.Value.ToString();

                // Position: center of the block face, offset outward slightly
                float e = 0.02f;
                Vector3 center = new Vector3(p.X + 0.5f, p.Y + 0.5f, p.Z + 0.5f);

                // right and down vectors for the face, text size per character
                float size = 0.04f;
                Vector3 right, down;

                switch (activeFace)
                {
                    case 0: // +Z
                        center.Z = p.Z + 1f + e;
                        right = new Vector3(size, 0, 0);
                        down = new Vector3(0, -size, 0);
                        break;
                    case 1: // +X
                        center.X = p.X + 1f + e;
                        right = new Vector3(0, 0, -size);
                        down = new Vector3(0, -size, 0);
                        break;
                    case 2: // -Z
                        center.Z = p.Z - e;
                        right = new Vector3(-size, 0, 0);
                        down = new Vector3(0, -size, 0);
                        break;
                    case 3: // -X
                        center.X = p.X - e;
                        right = new Vector3(0, 0, size);
                        down = new Vector3(0, -size, 0);
                        break;
                    case 4: // +Y
                        center.Y = p.Y + 1f + e;
                        right = new Vector3(size, 0, 0);
                        down = new Vector3(0, 0, -size);
                        break;
                    case 5: // -Y
                        center.Y = p.Y - e;
                        right = new Vector3(size, 0, 0);
                        down = new Vector3(0, 0, size);
                        break;
                    default:
                        continue;
                }

                fontBatch.QueueText(text, center, right, down, color, TextAnchor.HorizontalCenter | TextAnchor.VerticalCenter);
            }
        }

        // Draw active face per-block: WireThrough blocks get greenColor, others get defaultColor
        private void DrawFacePerBlock(FlatBatch3D flatBatch, BoundingBox box, int face, Color greenColor, Color defaultColor)
        {
            int minIX = (int)box.Min.X;
            int minIY = (int)box.Min.Y;
            int minIZ = (int)box.Min.Z;
            int maxIX = (int)box.Max.X - 1;
            int maxIY = (int)box.Max.Y - 1;
            int maxIZ = (int)box.Max.Z - 1;

            switch (face)
            {
                case 0: // +Z face: blocks at z=maxIZ
                    for (int x = minIX; x <= maxIX; x++)
                        for (int y = minIY; y <= maxIY; y++)
                        {
                            Color c = IsWireThroughConnectedAt(x, y, maxIZ, face) ? greenColor : defaultColor;
                            DrawSingleBlockFace(flatBatch, x, y, maxIZ, face, c);
                        }
                    break;
                case 1: // +X face: blocks at x=maxIX
                    for (int y = minIY; y <= maxIY; y++)
                        for (int z = minIZ; z <= maxIZ; z++)
                        {
                            Color c = IsWireThroughConnectedAt(maxIX, y, z, face) ? greenColor : defaultColor;
                            DrawSingleBlockFace(flatBatch, maxIX, y, z, face, c);
                        }
                    break;
                case 2: // -Z face: blocks at z=minIZ
                    for (int x = minIX; x <= maxIX; x++)
                        for (int y = minIY; y <= maxIY; y++)
                        {
                            Color c = IsWireThroughConnectedAt(x, y, minIZ, face) ? greenColor : defaultColor;
                            DrawSingleBlockFace(flatBatch, x, y, minIZ, face, c);
                        }
                    break;
                case 3: // -X face: blocks at x=minIX
                    for (int y = minIY; y <= maxIY; y++)
                        for (int z = minIZ; z <= maxIZ; z++)
                        {
                            Color c = IsWireThroughConnectedAt(minIX, y, z, face) ? greenColor : defaultColor;
                            DrawSingleBlockFace(flatBatch, minIX, y, z, face, c);
                        }
                    break;
                case 4: // +Y face: blocks at y=maxIY
                    for (int x = minIX; x <= maxIX; x++)
                        for (int z = minIZ; z <= maxIZ; z++)
                        {
                            Color c = IsWireThroughConnectedAt(x, maxIY, z, face) ? greenColor : defaultColor;
                            DrawSingleBlockFace(flatBatch, x, maxIY, z, face, c);
                        }
                    break;
                case 5: // -Y face: blocks at y=minIY
                    for (int x = minIX; x <= maxIX; x++)
                        for (int z = minIZ; z <= maxIZ; z++)
                        {
                            Color c = IsWireThroughConnectedAt(x, minIY, z, face) ? greenColor : defaultColor;
                            DrawSingleBlockFace(flatBatch, x, minIY, z, face, c);
                        }
                    break;
            }
        }

        // Draw a single 1x1 block face quad at block (bx,by,bz) on the specified face
        private void DrawSingleBlockFace(FlatBatch3D flatBatch, int bx, int by, int bz, int face, Color color)
        {
            float e = 0.01f;
            float x0 = bx, y0 = by, z0 = bz;
            float x1 = bx + 1, y1 = by + 1, z1 = bz + 1;

            switch (face)
            {
                case 0: // +Z
                    flatBatch.QueueQuad(
                        new Vector3(x0, y0, z1 + e), new Vector3(x1, y0, z1 + e),
                        new Vector3(x1, y1, z1 + e), new Vector3(x0, y1, z1 + e), color);
                    break;
                case 1: // +X
                    flatBatch.QueueQuad(
                        new Vector3(x1 + e, y0, z0), new Vector3(x1 + e, y0, z1),
                        new Vector3(x1 + e, y1, z1), new Vector3(x1 + e, y1, z0), color);
                    break;
                case 2: // -Z
                    flatBatch.QueueQuad(
                        new Vector3(x1, y0, z0 - e), new Vector3(x0, y0, z0 - e),
                        new Vector3(x0, y1, z0 - e), new Vector3(x1, y1, z0 - e), color);
                    break;
                case 3: // -X
                    flatBatch.QueueQuad(
                        new Vector3(x0 - e, y0, z1), new Vector3(x0 - e, y0, z0),
                        new Vector3(x0 - e, y1, z0), new Vector3(x0 - e, y1, z1), color);
                    break;
                case 4: // +Y
                    flatBatch.QueueQuad(
                        new Vector3(x0, y1 + e, z0), new Vector3(x1, y1 + e, z0),
                        new Vector3(x1, y1 + e, z1), new Vector3(x0, y1 + e, z1), color);
                    break;
                case 5: // -Y
                    flatBatch.QueueQuad(
                        new Vector3(x0, y0 - e, z1), new Vector3(x1, y0 - e, z1),
                        new Vector3(x1, y0 - e, z0), new Vector3(x0, y0 - e, z0), color);
                    break;
            }
        }

        // Draw a single interface face (semi-transparent red, alpha=0.3)
        // Only one face per interface cell
        private void DrawInterfaceFace(FlatBatch3D flatBatch, Point3 p, int face)
        {
            Color red = new Color(255, 0, 0, 77); // 255*0.3 �?77
            DrawSingleBlockFace(flatBatch, p.X, p.Y, p.Z, face, red);
        }

        // WireThrough block indices
        // Source: WireThroughPlanksBlock.cs Index=153, WireThroughStoneBlock.cs Index=154,
        //         WireThroughSemiconductorBlock.cs Index=155, WireThroughBricksBlock.cs Index=223,
        //         WireThroughCobblestoneBlock.cs Index=243
        private static readonly HashSet<int> WireThroughIndices = new HashSet<int>
        {
            153, 154, 155, 223, 243
        };

        // Check if block at (x,y,z) is a WireThrough block with wire on the specified face
        // WireThroughBlock: GetWiredFace(data) returns 0(Z-axis), 1(X-axis), or 4(Y-axis)
        // Wire passes through wiredFace and OppositeFace(wiredFace)
        // A face is connected if it equals wiredFace or OppositeFace(wiredFace)
        private bool IsWireThroughConnectedAt(int x, int y, int z, int face)
        {
            int cellValue = m_subsystemTerrain.Terrain.GetCellValue(x, y, z);
            int contents = Terrain.ExtractContents(cellValue);
            if (!WireThroughIndices.Contains(contents)) return false;
            int data = Terrain.ExtractData(cellValue);
            int wiredFace = WireThroughBlock.GetWiredFace(data);
            // Wire passes through wiredFace and its opposite
            return face == wiredFace || face == CellFace.OppositeFace(wiredFace);
        }

        // ============================================================
        // 3D Wiring: Routing Rules Implementation
        // Based on ROUTING_RULES.md — 8 rules for converting path to blocks
        // ============================================================

        // Routing decision for a single cell
        private class RoutingCell
        {
            public Point3 Position;
            public bool IsWireThrough;          // true=穿线块, false=导线块
            public int WireThroughWiredFace;    // 穿线块导通面: 0(Z), 1(X), 4(Y)
            public int WireFacesBitmask;        // 导线块: 6bit面掩码 (bit i=1 → Face i有导线点)
            public int WireThroughContentIndex; // 穿线块具体类型(153/154/155/223/243)
            public bool IsSupplement;           // 是否为补导线点(Z干涉处理产生)
            public int InFace;                  // 进面 (-1=none)
            public int OutFace;                 // 出面 (-1=none)
        }

        // Default WireThrough content type: Stone (154)
        private const int DefaultWireThroughContentIndex = 154;

        // Face direction vectors (same as CellFace.FaceToPoint3)
        private static readonly Point3[] FaceDirections = new Point3[]
        {
            new Point3(0, 0, 1),   // Face 0: +Z (下 in俯视图)
            new Point3(1, 0, 0),   // Face 1: +X (右)
            new Point3(0, 0, -1),  // Face 2: -Z (上)
            new Point3(-1, 0, 0),  // Face 3: -X (左)
            new Point3(0, 1, 0),   // Face 4: +Y
            new Point3(0, -1, 0)   // Face 5: -Y
        };

        /// <summary>
        /// Get face index pointing from 'from' toward 'to' (must be adjacent)
        /// </summary>
        private static int GetFaceToward(Point3 from, Point3 to)
        {
            Point3 dir = new Point3(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
            for (int i = 0; i < 6; i++)
                if (dir.Equals(FaceDirections[i])) return i;
            return -1;
        }

        /// <summary>
        /// Check if two faces are opposite (0↔2, 1↔3, 4↔5)
        /// </summary>
        private static bool AreOppositeFaces(int face1, int face2)
        {
            return CellFace.OppositeFace(face1) == face2;
        }

        // Map a face to WireThrough wiredFace for SetWiredFace:
        // Face 0 or 2 - wiredFace=0 (Z axis, data bitwise-and 3 = 0)
        // Face 1 or 3 - wiredFace=1 (X axis, data bitwise-and 3 = 1)
        // Face 4 or 5 - wiredFace=4 (Y axis, data bitwise-and 3 = 2)
        private static int FaceToWiredFace(int face)
        {
            if (face == 0 || face == 2) return 0;
            if (face == 1 || face == 3) return 1;
            return 4; // face 4 or 5
        }

        /// <summary>
        /// Detect WireThrough content type from existing block at a position, or return default
        /// </summary>
        private int DetectWireThroughContentType(Point3 pos)
        {
            int cellValue = m_subsystemTerrain.Terrain.GetCellValue(pos.X, pos.Y, pos.Z);
            int contents = Terrain.ExtractContents(cellValue);
            if (WireThroughIndices.Contains(contents))
                return contents;
            return DefaultWireThroughContentIndex;
        }

        /// &lt;summary&gt;
        /// Determine routing decisions for a path, applying all 8 rules from ROUTING_RULES.md
        /// &lt;/summary&gt;
        /// &lt;param name="path"&gt;List of basic block positions (from FindPath)&lt;/param&gt;
        /// &lt;param name="startFace"&gt;Active face of start area (wire enters from this face direction)&lt;/param&gt;
        /// &lt;param name="endFace"&gt;Active face of end area (wire exits toward this face direction)&lt;/param&gt;
        /// &lt;returns&gt;List of RoutingCell including basic blocks and supplement wire points&lt;/returns&gt;
        private List<RoutingCell> DetermineRouting(List<Point3> path, int startFace, int endFace)
        {
            var cells = new List<RoutingCell>();
            if (path.Count == 0) return cells;

            // Step 1: Calculate in/out faces for each basic block
            // Rule 2: opposite faces → WireThrough, non-opposite → WireBlock
            for (int i = 0; i < path.Count; i++)
            {
                var cell = new RoutingCell
                {
                    Position = path[i],
                    IsSupplement = false,
                    InFace = -1,
                    OutFace = -1,
                    WireFacesBitmask = 0,
                    WireThroughWiredFace = 0,
                    WireThroughContentIndex = DefaultWireThroughContentIndex
                };

                                // In face: which face of this cell faces toward the previous block
                // Need OppositeFace because GetFaceToward gives direction vector,
                // but wire point sits on the face looking toward the neighbor (opposite side)
                if (i == 0)
                {
                    cell.InFace = startFace;
                }
                else
                {
                    cell.InFace = CellFace.OppositeFace(GetFaceToward(path[i], path[i - 1]));
                }

                // Out face: which face of this cell faces toward the next block
                if (i == path.Count - 1)
                {
                    cell.OutFace = endFace;
                }
                else
                {
                    cell.OutFace = CellFace.OppositeFace(GetFaceToward(path[i], path[i + 1]));
                }

                // Rule 2: Determine block type based on in/out face relationship
                if (AreOppositeFaces(cell.InFace, cell.OutFace))
                {
                    // Opposite faces → WireThrough (规则2)
                    cell.IsWireThrough = true;
                    cell.WireThroughWiredFace = FaceToWiredFace(cell.OutFace);
                    cell.WireThroughContentIndex = DetectWireThroughContentType(path[i]);
                }
                else
                {
                    // Non-opposite (turn) → WireBlock (规则2)
                    cell.IsWireThrough = false;
                    cell.WireFacesBitmask = (1 << cell.InFace) | (1 << cell.OutFace);
                }

                cells.Add(cell);
            }

            // Step 2: Z interference detection and processing (规则3+4)
            // Process in path order: "先处理再考虑连接"
            ProcessZInterference(cells, path);

            return cells;
        }

        // Rules 3+4: Detect and process Z interference between adjacent wire blocks
        // Z interference = two adjacent cells both wire blocks, face-to-face lacks entity support
        // Processing: change the LATER cell to WireThrough, add supplement wire point
        private void ProcessZInterference(List<RoutingCell> cells, List<Point3> path)
        {
            // We need to track supplement positions to avoid duplicates
            var supplementPositions = new HashSet<Point3>();

            for (int i = 0; i < cells.Count - 1; i++)
            {
                RoutingCell cell1 = cells[i];
                RoutingCell cell2 = cells[i + 1];

                // 规则3: Z interference = both are wire blocks and adjacent
                if (cell1.IsWireThrough || cell2.IsWireThrough || cell1.IsSupplement || cell2.IsSupplement)
                    continue; // At least one is WireThrough or supplement → no Z interference

                // Check if they are adjacent (they should be since they're consecutive in path)
                Point3 diff = new Point3(
                    cell2.Position.X - cell1.Position.X,
                    cell2.Position.Y - cell1.Position.Y,
                    cell2.Position.Z - cell1.Position.Z);
                bool adjacent = false;
                foreach (var dir in FaceDirections)
                    if (diff.Equals(dir)) { adjacent = true; break; }
                if (!adjacent) continue;

                // Z interference detected! Apply 规则4:
                // 1. Change the LATER cell (cell2) to WireThrough
                // 2. WireThrough conduction direction = toward next basic block
                // 3. Add supplement wire point at conduction reverse end

                int nextBasicFace; // direction from cell2 toward next basic block
                if (i + 2 < cells.Count)
                {
                    nextBasicFace = CellFace.OppositeFace(GetFaceToward(cell2.Position, cells[i + 2].Position));
                }
                else
                {
                    // Last cell: use its out face
                    nextBasicFace = cell2.OutFace;
                }

                int wiredFace = FaceToWiredFace(nextBasicFace);
                int reverseFace = CellFace.OppositeFace(wiredFace);

                // Change cell2 to WireThrough
                cell2.IsWireThrough = true;
                cell2.WireThroughWiredFace = wiredFace;
                cell2.WireThroughContentIndex = DetectWireThroughContentType(cell2.Position);
                cell2.WireFacesBitmask = 0;

                // 补点位置: 穿线块 + 导通反向端方向
                // Same reversal: use opposite face direction to find supplement position
                int reverseDirFace = CellFace.OppositeFace(reverseFace);
                Point3 supplementPos = new Point3(
                    cell2.Position.X + FaceDirections[reverseDirFace].X,
                    cell2.Position.Y + FaceDirections[reverseDirFace].Y,
                    cell2.Position.Z + FaceDirections[reverseDirFace].Z);

                if (!supplementPositions.Contains(supplementPos))
                {
                    supplementPositions.Add(supplementPos);

                    // Supplement wire point: 1 face only,贴穿线块面
                    // The face on the supplement that faces toward the WireThrough block
                    // Same reversal as InFace/OutFace: use reverseFace directly
                    // (because the face-to-bitmask mapping is reversed, matching the
                    // OppositeFace correction applied to InFace/OutFace)
                    int supplementFace = reverseFace;

                    var supplement = new RoutingCell
                    {
                        Position = supplementPos,
                        IsWireThrough = false,
                        IsSupplement = true,
                        WireFacesBitmask = 1 << supplementFace, // 仅1个面 (规则4 step 5)
                        InFace = -1,
                        OutFace = -1,
                        WireThroughContentIndex = 0
                    };

                    // Insert supplement after cell2 in the list
                    cells.Insert(i + 2, supplement);

                    Log.Information($"[CircuitAutoRouter] Z interference: ({cell1.Position.X},{cell1.Position.Y},{cell1.Position.Z}) & ({cell2.Position.X},{cell2.Position.Y},{cell2.Position.Z}) → cell2→WireThrough(wiredFace={wiredFace}), supplement at ({supplementPos.X},{supplementPos.Y},{supplementPos.Z}) face={supplementFace}");
                }
                else
                {
                    Log.Information($"[CircuitAutoRouter] Z interference: ({cell1.Position.X},{cell1.Position.Y},{cell1.Position.Z}) & ({cell2.Position.X},{cell2.Position.Y},{cell2.Position.Z}) → supplement position ({supplementPos.X},{supplementPos.Y},{supplementPos.Z}) already occupied, skipped");
                }
            }
        }

        // Place wire blocks in terrain based on routing decisions
        // Merges wire face bitmasks if a cell already has a WireBlock
        private void PlaceWires(List<RoutingCell> routingCells)
        {
            // Group cells by position to merge wire face bitmasks
            var cellMap = new Dictionary<Point3, RoutingCell>();

            foreach (var cell in routingCells)
            {
                if (cellMap.TryGetValue(cell.Position, out RoutingCell existing))
                {
                    // Merge: if both are wire blocks, combine face bitmasks
                    if (!existing.IsWireThrough && !cell.IsWireThrough)
                    {
                        existing.WireFacesBitmask |= cell.WireFacesBitmask;
                    }
                    // If one is WireThrough and other is wire, WireThrough takes priority
                    else if (cell.IsWireThrough)
                    {
                        cellMap[cell.Position] = cell;
                    }
                    // else keep existing (existing is WireThrough)
                }
                else
                {
                    cellMap[cell.Position] = cell;
                }
            }

            foreach (var kv in cellMap)
            {
                RoutingCell cell = kv.Value;
                Point3 pos = cell.Position;

                if (cell.IsWireThrough)
                {
                    // Place WireThrough block
                    // WireThroughBlock.SetWiredFace: wiredFace 0→data&3=0, 1→data&3=1, 4→data&3=2
                    int data = WireThroughBlock.SetWiredFace(0, cell.WireThroughWiredFace);
                    int value = Terrain.MakeBlockValue(cell.WireThroughContentIndex, 0, data);
                    m_subsystemTerrain.ChangeCell(pos.X, pos.Y, pos.Z, value);
                    Log.Information($"[CircuitAutoRouter] Place WireThrough at ({pos.X},{pos.Y},{pos.Z}) content={cell.WireThroughContentIndex} wiredFace={cell.WireThroughWiredFace}");
                }
                else
                {
                    // Place WireBlock (or merge with existing)
                    int existingValue = m_subsystemTerrain.Terrain.GetCellValue(pos.X, pos.Y, pos.Z);
                    int existingContents = Terrain.ExtractContents(existingValue);
                    int existingBitmask = (existingContents == WireBlock.Index)
                        ? WireBlock.GetWireFacesBitmask(existingValue)
                        : 0;
                    int newBitmask = existingBitmask | cell.WireFacesBitmask;

                    if (newBitmask == 0) continue; // No faces to place

                    int value = WireBlock.SetWireFacesBitmask(
                        Terrain.MakeBlockValue(WireBlock.Index), newBitmask);
                    m_subsystemTerrain.ChangeCell(pos.X, pos.Y, pos.Z, value);

                    // Log which faces are set
                    var faces = new List<string>();
                    for (int f = 0; f < 6; f++)
                        if ((newBitmask & (1 << f)) != 0) faces.Add(FaceNames[f]);
                    Log.Information($"[CircuitAutoRouter] Place WireBlock at ({pos.X},{pos.Y},{pos.Z}) faces=[{string.Join(",", faces)}] supplement={cell.IsSupplement}");
                }
            }
        }

        // ============================================================
        // End 3D Wiring
        // ============================================================

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(true);
            m_subsystemGameWidgets = Project.FindSubsystem<SubsystemGameWidgets>(true);
            Log.Information("[CircuitAutoRouter] SubsystemCircuitRouter loaded");
        }
    }
}
