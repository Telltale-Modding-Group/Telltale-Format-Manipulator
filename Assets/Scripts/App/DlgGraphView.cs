using System;
using System.Collections.Generic;
using TFM.Core.Meta;
using UnityEngine;

namespace TFM.App
{
    /// <summary>
    /// Node graph of a dialogue. Nodes and branches (choices, cases, sequence elements, folder
    /// entries) are boxes; mNext and branch links are drawn as arrows top-to-bottom in a tree
    /// layout; jumps are drawn as separate arrows to their targets. Pan by dragging, zoom with
    /// the wheel, click to select, double-click a jump to go to its target.
    /// Positions are not stored in .dlog files, so the layout is computed: a tidy tree where every
    /// node sits above its branches and its next node.
    /// </summary>
    public sealed class DlgGraphView
    {
        public enum BoxKind { Node, Branch, Folder, Group }

        public sealed class Box
        {
            public MetaValue Obj;
            public BoxKind Kind;
            public string Title = "", Body = "", Cond;
            public Color Color = Color.gray;
            public float X, Y, W, H;
            public float SubW;
            public readonly List<Box> Kids = new List<Box>();
            public Box Parent;
            /// <summary>A jump that is drawn as a separate arrow: its target box.</summary>
            public Box JumpTarget;
            /// <summary>Kid index from which kids are "after" continuations rather than branches.</summary>
            public bool NextIsKid;
        }

        /// <summary>Describes a node or branch for its box: title, body text, condition (may be null).</summary>
        public Func<MetaValue, BoxKind, string[]> Describe;
        public Func<MetaValue, Color> ColorOf;
        public Action<MetaValue> OnSelect;
        /// <summary>Called on double-click of a box; return true when handled.</summary>
        public Func<MetaValue, bool> OnActivate;

        public bool FollowJumps;
        public bool ShowJumpArrows = true;

        readonly List<Box> _roots = new List<Box>();
        readonly List<Box> _all = new List<Box>();
        readonly Dictionary<MetaValue, Box> _byObj = new Dictionary<MetaValue, Box>();
        DlgDocument _doc;
        bool _dirty = true;

        Vector2 _pan;          // world coordinate at the canvas's top-left corner
        float _zoom = 1f;
        bool _panning;
        Vector2 _panMouse0, _pan0;
        MetaValue _selected;
        Rect _lastCanvas;
        bool _fitPending = true;
        MetaValue _focusPending;

        const float NodeW = 270f, NodeH = 78f, BranchW = 220f, BranchH = 54f, FolderH = 40f;
        const float HGap = 26f, VGap = 34f;

        static Texture2D _tri;

        public int Count { get { return _all.Count; } }

        public MetaValue Selected { get { return _selected; } set { _selected = value; } }

        public void SetDocument(DlgDocument doc)
        {
            _doc = doc;
            _dirty = true;
            _fitPending = true;
            _selected = null;
        }

        /// <summary>Rebuild the layout on the next draw (after structural edits or option changes).</summary>
        public void Invalidate() { _dirty = true; }

        /// <summary>Selects and scrolls to a node or branch.</summary>
        public void Focus(MetaValue obj)
        {
            _selected = obj;
            _focusPending = obj;
        }

        public void Fit() { _fitPending = true; }

        public bool Contains(MetaValue obj) { return obj != null && _byObj.ContainsKey(obj); }

        // ================================================================ layout
        void Build()
        {
            _dirty = false;
            _roots.Clear();
            _all.Clear();
            _byObj.Clear();
            if (_doc == null) return;

            // Folder entries reached by a jump: in flow mode they hang under that jump.
            HashSet<MetaValue> jumpReached = new HashSet<MetaValue>();
            if (FollowJumps)
                for (int i = 0; i < _doc.Nodes.Count; i++)
                {
                    MetaValue entry = JumpEntry(_doc.Nodes[i]);
                    if (entry != null) jumpReached.Add(entry);
                }

            for (int f = 0; f < _doc.Folders.Count; f++)
            {
                MetaValue folder = _doc.Folders[f];
                Box fb = NewBox(folder, BoxKind.Folder);
                foreach (DlgDocument.ChildSetInfo set in DlgDocument.ChildSets(folder))
                    foreach (MetaValue c in set.Children)
                    {
                        if (FollowJumps && jumpReached.Contains(c)) continue;
                        Box cb = VisitBranch(c);
                        if (cb != null) Attach(fb, cb);
                    }
                _roots.Add(fb);
            }

            // Folder entries reached only by jumps that were never placed (e.g. jump cycles).
            if (FollowJumps)
                for (int f = 0; f < _doc.Folders.Count; f++)
                    foreach (DlgDocument.ChildSetInfo set in DlgDocument.ChildSets(_doc.Folders[f]))
                        foreach (MetaValue c in set.Children)
                        {
                            if (_byObj.ContainsKey(c)) continue;
                            Box cb = VisitBranch(c);
                            if (cb != null) Attach(_roots[f], cb);
                        }

            // Nodes nothing links to (reached by jumps into the middle of a chain, or orphans).
            Box loose = null;
            for (int i = 0; i < _doc.Nodes.Count; i++)
            {
                MetaValue n = _doc.Nodes[i];
                if (_byObj.ContainsKey(n)) continue;
                MetaValue prev = _doc.NodeById(DlgDocument.LinkId(DlgDocument.PrevLink(n)));
                if (prev != null && !_byObj.ContainsKey(prev) && prev != n) continue; // placed with its chain head
                if (loose == null)
                {
                    loose = new Box { Kind = BoxKind.Group, Title = "Not linked from any folder", Body = "Chains reached only by jumps, or unused.", Color = new Color(0.3f, 0.3f, 0.33f), W = NodeW, H = FolderH + 14f };
                    _all.Add(loose);
                    _roots.Add(loose);
                }
                Box nb = VisitChain(n);
                if (nb != null) Attach(loose, nb);
            }

            // Anything still unplaced sits in a loop of nodes pointing at each other.
            for (int i = 0; i < _doc.Nodes.Count; i++)
            {
                MetaValue n = _doc.Nodes[i];
                if (_byObj.ContainsKey(n)) continue;
                if (loose == null)
                {
                    loose = new Box { Kind = BoxKind.Group, Title = "Not linked from any folder", Body = "Chains reached only by jumps, or unused.", Color = new Color(0.3f, 0.3f, 0.33f), W = NodeW, H = FolderH + 14f };
                    _all.Add(loose);
                    _roots.Add(loose);
                }
                Box nb = VisitChain(n);
                if (nb != null) Attach(loose, nb);
            }

            // Jump arrows
            for (int i = 0; i < _all.Count; i++)
            {
                Box b = _all[i];
                if (b.Obj == null || b.Obj.Type != "DlgNodeJump") continue;
                MetaValue t = JumpTargetObj(b.Obj);
                Box tb;
                if (t != null && _byObj.TryGetValue(t, out tb) && !(b.Kids.Count > 0 && ReferenceEquals(b.Kids[b.Kids.Count - 1], tb)))
                    b.JumpTarget = tb;
            }

            // Measure and place each root side by side.
            float x = 0f;
            for (int r = 0; r < _roots.Count; r++)
            {
                Measure(_roots[r]);
                Place(_roots[r], x, 0f);
                x += _roots[r].SubW + HGap * 3f;
            }
        }

        Box NewBox(MetaValue obj, BoxKind kind)
        {
            Box b = new Box { Obj = obj, Kind = kind };
            string[] d = Describe != null ? Describe(obj, kind) : new[] { obj.Type, "", null };
            b.Title = d[0] ?? "";
            b.Body = d.Length > 1 ? d[1] ?? "" : "";
            b.Cond = d.Length > 2 ? d[2] : null;
            b.Color = ColorOf != null ? ColorOf(obj) : Color.gray;
            b.W = kind == BoxKind.Branch ? BranchW : NodeW;
            b.H = kind == BoxKind.Branch ? BranchH : kind == BoxKind.Folder ? FolderH : NodeH;
            if (!string.IsNullOrEmpty(b.Cond)) b.H += 16f;
            if (obj != null) _byObj[obj] = b;
            _all.Add(b);
            return b;
        }

        static void Attach(Box parent, Box kid)
        {
            kid.Parent = parent;
            parent.Kids.Add(kid);
        }

        Box VisitBranch(MetaValue child)
        {
            if (_byObj.ContainsKey(child)) return null;
            Box cb = NewBox(child, BoxKind.Branch);
            MetaValue first = _doc.NodeById(DlgDocument.LinkId(DlgDocument.ChildLink(child)));
            if (first != null)
            {
                Box fb = VisitChain(first);
                if (fb != null) Attach(cb, fb);
            }
            return cb;
        }

        /// <summary>Places a node and everything after it. Iterative along mNext so long chains do not recurse deeply.</summary>
        Box VisitChain(MetaValue start)
        {
            if (start == null || _byObj.ContainsKey(start)) return null;
            Box head = null, prev = null;
            MetaValue n = start;
            while (n != null && !_byObj.ContainsKey(n))
            {
                Box b = NewBox(n, BoxKind.Node);
                if (head == null) head = b;
                if (prev != null) { Attach(prev, b); prev.NextIsKid = true; }
                foreach (DlgDocument.ChildSetInfo set in DlgDocument.ChildSets(n))
                    foreach (MetaValue c in set.Children)
                    {
                        Box cb = VisitBranch(c);
                        if (cb != null) Attach(b, cb);
                    }
                if (FollowJumps && n.Type == "DlgNodeJump")
                {
                    MetaValue entry = JumpEntry(n);
                    if (entry != null && !_byObj.ContainsKey(entry))
                    {
                        Box eb = VisitBranch(entry);
                        if (eb != null) Attach(b, eb);
                    }
                }
                prev = b;
                n = _doc.NodeById(DlgDocument.LinkId(DlgDocument.NextLink(n)));
            }
            return head;
        }

        /// <summary>The object a jump leads to in this dialogue (node or branch), or null.</summary>
        MetaValue JumpTargetObj(MetaValue jump)
        {
            MetaValue dlg = jump["mhJumpToDlg"];
            if (dlg != null && dlg.Bits != 0) return null;
            MetaValue nm = jump["mJumpToName"];
            if (nm == null || nm.Bits == 0) return null;
            List<MetaValue> t = _doc.ByName(nm.Bits);
            return t.Count > 0 ? t[0] : null;
        }

        /// <summary>For flow layout: the folder entry whose conversation a jump leads into, or null.</summary>
        MetaValue JumpEntry(MetaValue jump)
        {
            if (jump.Type != "DlgNodeJump") return null;
            MetaValue t = JumpTargetObj(jump);
            if (t == null) return null;
            DlgDocument.ChildInfo ci = _doc.ChildInfoOf(t);
            if (ci == null)
            {
                ci = _doc.ChainHead(t);
                if (ci == null) return null;
            }
            return _doc.FolderOfChild(ci.Child) != null ? ci.Child : null;
        }

        static void Measure(Box b)
        {
            // Post-order without recursion: long chains are thousands deep.
            List<Box> order = new List<Box>();
            Stack<Box> st = new Stack<Box>();
            st.Push(b);
            while (st.Count > 0)
            {
                Box x = st.Pop();
                order.Add(x);
                for (int i = 0; i < x.Kids.Count; i++) st.Push(x.Kids[i]);
            }
            for (int i = order.Count - 1; i >= 0; i--)
            {
                Box x = order[i];
                float sum = 0f;
                for (int k = 0; k < x.Kids.Count; k++) sum += x.Kids[k].SubW + (k > 0 ? HGap : 0f);
                x.SubW = Mathf.Max(x.W, sum);
            }
        }

        static void Place(Box root, float left, float top)
        {
            Stack<KeyValuePair<Box, Vector2>> st = new Stack<KeyValuePair<Box, Vector2>>();
            st.Push(new KeyValuePair<Box, Vector2>(root, new Vector2(left, top)));
            while (st.Count > 0)
            {
                KeyValuePair<Box, Vector2> e = st.Pop();
                Box b = e.Key;
                b.X = e.Value.x + (b.SubW - b.W) * 0.5f;
                b.Y = e.Value.y;
                float sum = 0f;
                for (int k = 0; k < b.Kids.Count; k++) sum += b.Kids[k].SubW + (k > 0 ? HGap : 0f);
                float cx = e.Value.x + (b.SubW - sum) * 0.5f;
                for (int k = 0; k < b.Kids.Count; k++)
                {
                    st.Push(new KeyValuePair<Box, Vector2>(b.Kids[k], new Vector2(cx, b.Y + b.H + VGap)));
                    cx += b.Kids[k].SubW + HGap;
                }
            }
        }

        Rect Bounds()
        {
            if (_all.Count == 0) return new Rect(0, 0, 100, 100);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < _all.Count; i++)
            {
                Box b = _all[i];
                x0 = Mathf.Min(x0, b.X); y0 = Mathf.Min(y0, b.Y);
                x1 = Mathf.Max(x1, b.X + b.W); y1 = Mathf.Max(y1, b.Y + b.H);
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        // ================================================================ drawing
        static GUIStyle _title, _body, _cond;
        static int _styleSize = -1;

        static void Styles(float zoom)
        {
            int size = Mathf.Max(1, Mathf.RoundToInt(12f * zoom));
            if (_title != null && _styleSize == size) return;
            _styleSize = size;
            _title = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            _title.normal.textColor = Color.white;
            _body = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true, clipping = TextClipping.Clip, alignment = TextAnchor.UpperLeft };
            _body.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            _cond = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(1, size - 1), wordWrap = false, clipping = TextClipping.Clip };
            _cond.normal.textColor = new Color(1f, 0.78f, 0.35f);
            _title.padding = _body.padding = _cond.padding = new RectOffset(0, 0, 0, 0);
            _title.margin = _body.margin = _cond.margin = new RectOffset(0, 0, 0, 0);
        }

        static Texture2D Tri()
        {
            if (_tri != null) return _tri;
            const int n = 15;
            _tri = new Texture2D(n, n, TextureFormat.RGBA32, false);
            _tri.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // Downward-pointing triangle: wide at the top row, a point at the bottom.
                    float half = (y + 0.5f) / n * (n * 0.5f);
                    bool inside = Mathf.Abs(x + 0.5f - n * 0.5f) <= half;
                    _tri.SetPixel(x, y, inside ? Color.white : new Color(1, 1, 1, 0));
                }
            _tri.Apply();
            return _tri;
        }

        static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        Vector2 ToScreen(float x, float y) { return new Vector2((x - _pan.x) * _zoom, (y - _pan.y) * _zoom); }

        Vector2 ToWorld(Vector2 p) { return new Vector2(p.x / _zoom + _pan.x, p.y / _zoom + _pan.y); }

        void LineH(float x0, float x1, float y, Color c, float t)
        {
            Vector2 a = ToScreen(Mathf.Min(x0, x1), y), b = ToScreen(Mathf.Max(x0, x1), y);
            Fill(new Rect(a.x, a.y - t * 0.5f, Mathf.Max(1f, b.x - a.x), t), c);
        }

        void LineV(float x, float y0, float y1, Color c, float t)
        {
            Vector2 a = ToScreen(x, Mathf.Min(y0, y1)), b = ToScreen(x, Mathf.Max(y0, y1));
            Fill(new Rect(a.x - t * 0.5f, a.y, t, Mathf.Max(1f, b.y - a.y)), c);
        }

        void ArrowDown(float x, float y, Color c)
        {
            float s = Mathf.Max(5f, 9f * _zoom);
            Vector2 p = ToScreen(x, y);
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - s * 0.5f, p.y - s, s, s), Tri());
            GUI.color = old;
        }

        /// <summary>Draws the graph inside <paramref name="canvas"/> (GUI coordinates) and handles its input.</summary>
        public void OnGUI(Rect canvas)
        {
            if (_dirty) Build();
            _lastCanvas = canvas;
            if (_fitPending && canvas.width > 10f) { DoFit(canvas); _fitPending = false; }
            if (_focusPending != null && canvas.width > 10f)
            {
                Box fb;
                if (_byObj.TryGetValue(_focusPending, out fb))
                {
                    _zoom = Mathf.Max(_zoom, 0.6f);
                    _pan = new Vector2(fb.X + fb.W * 0.5f - canvas.width * 0.5f / _zoom, fb.Y + fb.H * 0.5f - canvas.height * 0.4f / _zoom);
                }
                _focusPending = null;
            }

            Fill(canvas, new Color(0.11f, 0.11f, 0.13f));
            GUI.BeginGroup(canvas);
            Rect local = new Rect(0, 0, canvas.width, canvas.height);
            Rect view = Rect.MinMaxRect(_pan.x, _pan.y, _pan.x + canvas.width / _zoom, _pan.y + canvas.height / _zoom);
            Styles(_zoom * GuiUtil.Scale);

            Event e = Event.current;
            if (e.type == EventType.Repaint)
            {
                Color link = new Color(0.62f, 0.66f, 0.74f, 0.9f);
                float t = Mathf.Max(1f, 2f * _zoom);
                // Tree edges: parent bottom -> mid gap -> across -> child top.
                for (int i = 0; i < _all.Count; i++)
                {
                    Box b = _all[i];
                    if (b.Kids.Count == 0) continue;
                    float midY = b.Y + b.H + VGap * 0.5f;
                    float minX = float.MaxValue, maxX = float.MinValue;
                    for (int k = 0; k < b.Kids.Count; k++)
                    {
                        float cx = b.Kids[k].X + b.Kids[k].W * 0.5f;
                        minX = Mathf.Min(minX, cx); maxX = Mathf.Max(maxX, cx);
                    }
                    float px = b.X + b.W * 0.5f;
                    minX = Mathf.Min(minX, px); maxX = Mathf.Max(maxX, px);
                    if (maxX < view.xMin || minX > view.xMax || midY + VGap < view.yMin || b.Y > view.yMax) continue;
                    LineV(px, b.Y + b.H, midY, link, t);
                    if (b.Kids.Count > 1 || Mathf.Abs(minX - maxX) > 0.5f) LineH(minX, maxX, midY, link, t);
                    for (int k = 0; k < b.Kids.Count; k++)
                    {
                        Box c = b.Kids[k];
                        float cx = c.X + c.W * 0.5f;
                        LineV(cx, midY, c.Y, link, t);
                        ArrowDown(cx, c.Y, link);
                    }
                }
                // Jump arrows: out of the jump's right side, along a lane, into the target's top.
                if (ShowJumpArrows)
                {
                    Color jc = new Color(0.95f, 0.35f, 0.35f, 0.75f);
                    for (int i = 0; i < _all.Count; i++)
                    {
                        Box b = _all[i];
                        Box tb = b.JumpTarget;
                        if (tb == null) continue;
                        float sy = b.Y + b.H * 0.5f, sx = b.X + b.W;
                        float tx = tb.X + tb.W * 0.5f, ty = tb.Y;
                        float laneX = Mathf.Max(sx, tx + tb.W * 0.5f) + 14f + (i % 5) * 4f;
                        float laneY = ty - VGap * 0.3f;
                        float ymin = Mathf.Min(sy, laneY), ymax = Mathf.Max(sy, laneY);
                        float xmin = Mathf.Min(sx, tx), xmax = laneX;
                        if (xmax < view.xMin || xmin > view.xMax || ymax < view.yMin || ymin > view.yMax) continue;
                        LineH(sx, laneX, sy, jc, t);
                        LineV(laneX, sy, laneY, jc, t);
                        LineH(tx, laneX, laneY, jc, t);
                        LineV(tx, laneY, ty, jc, t);
                        ArrowDown(tx, ty, jc);
                    }
                }
            }

            // Boxes
            bool text = _zoom * GuiUtil.Scale >= 0.4f;
            for (int i = 0; i < _all.Count; i++)
            {
                Box b = _all[i];
                if (b.X > view.xMax || b.X + b.W < view.xMin || b.Y > view.yMax || b.Y + b.H < view.yMin) continue;
                Vector2 p = ToScreen(b.X, b.Y);
                Rect r = new Rect(p.x, p.y, b.W * _zoom, b.H * _zoom);
                bool sel = b.Obj != null && ReferenceEquals(b.Obj, _selected);
                if (e.type == EventType.Repaint)
                {
                    if (sel) Fill(new Rect(r.x - 3f, r.y - 3f, r.width + 6f, r.height + 6f), new Color(1f, 0.9f, 0.3f));
                    Fill(r, new Color(0.16f, 0.16f, 0.19f));
                    Fill(new Rect(r.x, r.y, r.width, Mathf.Max(2f, 18f * _zoom)), b.Color);
                    Fill(new Rect(r.x, r.y, Mathf.Max(2f, 4f * _zoom), r.height), b.Color);
                    if (text)
                    {
                        float pad = 6f * _zoom;
                        float lh = 15f * _zoom * GuiUtil.Scale;
                        GUI.Label(new Rect(r.x + pad, r.y + 1f * _zoom, r.width - pad * 2f, Mathf.Max(lh, 18f * _zoom)), b.Title, _title);
                        float y = r.y + 20f * _zoom;
                        if (!string.IsNullOrEmpty(b.Cond))
                        {
                            GUI.Label(new Rect(r.x + pad, y, r.width - pad * 2f, lh), b.Cond, _cond);
                            y += 16f * _zoom;
                        }
                        GUI.Label(new Rect(r.x + pad, y, r.width - pad * 2f, r.yMax - y - 2f * _zoom), b.Body, _body);
                    }
                }
                if (b.Obj != null && e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
                {
                    _selected = b.Obj;
                    if (OnSelect != null) OnSelect(b.Obj);
                    if (e.clickCount == 2)
                    {
                        if (b.JumpTarget != null) Focus(b.JumpTarget.Obj);
                        else if (OnActivate != null) OnActivate(b.Obj);
                    }
                    e.Use();
                }
            }

            // Pan and zoom
            if (e.type == EventType.MouseDown && local.Contains(e.mousePosition) && (e.button == 0 || e.button == 1 || e.button == 2))
            {
                _panning = true;
                _panMouse0 = e.mousePosition;
                _pan0 = _pan;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _panning)
            {
                _pan = _pan0 - (e.mousePosition - _panMouse0) / _zoom;
                e.Use();
            }
            else if (e.rawType == EventType.MouseUp && _panning)
            {
                _panning = false;
            }
            else if (e.type == EventType.ScrollWheel && local.Contains(e.mousePosition))
            {
                Vector2 before = ToWorld(e.mousePosition);
                float f = e.delta.y > 0 ? 1f / 1.15f : 1.15f;
                _zoom = Mathf.Clamp(_zoom * f, 0.05f, 2.5f);
                _pan = before - e.mousePosition / _zoom;
                e.Use();
            }
            GUI.EndGroup();

            if (_all.Count == 0)
                GUI.Label(new Rect(canvas.x + 10f, canvas.y + 10f, canvas.width - 20f, 40f), "This dialogue has no nodes.", GuiUtil.Note);
        }

        void DoFit(Rect canvas)
        {
            Rect b = Bounds();
            float z = Mathf.Min(canvas.width / (b.width + 80f), canvas.height / (b.height + 80f));
            // Very large dialogues fit to the first folder's width instead, so the start stays readable.
            _zoom = Mathf.Clamp(z, 0.25f, 1.2f);
            _pan = new Vector2(b.xMin - 40f, b.yMin - 40f);
            if (_roots.Count > 0 && z < 0.25f)
            {
                Box r0 = _roots[0];
                _pan = new Vector2(r0.X + r0.W * 0.5f - canvas.width * 0.5f / _zoom, r0.Y - 40f);
            }
        }

        public void ZoomBy(float f)
        {
            Vector2 center = new Vector2(_lastCanvas.width * 0.5f, _lastCanvas.height * 0.5f);
            Vector2 before = ToWorld(center);
            _zoom = Mathf.Clamp(_zoom * f, 0.05f, 2.5f);
            _pan = before - center / _zoom;
        }

        /// <summary>Boxes whose text contains <paramref name="q"/>, in layout order (left to right, top to bottom).</summary>
        public List<MetaValue> Find(string q)
        {
            List<Box> hits = new List<Box>();
            for (int i = 0; i < _all.Count; i++)
            {
                Box b = _all[i];
                if (b.Obj == null) continue;
                if ((b.Title + " " + b.Body + " " + b.Cond).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(b);
            }
            hits.Sort((a, c) => a.X != c.X ? a.X.CompareTo(c.X) : a.Y.CompareTo(c.Y));
            List<MetaValue> r = new List<MetaValue>(hits.Count);
            for (int i = 0; i < hits.Count; i++) r.Add(hits[i].Obj);
            return r;
        }
    }
}
