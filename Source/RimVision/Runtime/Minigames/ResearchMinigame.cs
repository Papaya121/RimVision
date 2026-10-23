using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    sealed class ResearchMinigame : WorkMinigame
    {
        const int Size = 4, Count = Size * Size, Source = Size;
        readonly System.Random _random = new System.Random();
        readonly int[] _nodes = new int[Count];
        readonly int[] _queue = new int[Count];
        int _powered, _connected, _score;
        bool _paused = true;
        float _cooldown;
        public override string Title => "Rim3D_MinigameResearch".Translate().ToString();
        public override string Instructions => "Rim3D_ResearchHint".Translate().ToString();
        public override int PointsPerReward => 1;
        public override int Score => _score;
        int Reward => _connected == 3 ? 4 : _connected;

        internal ResearchMinigame() { Restart(); }

        public override void Restart()
        {
            _score = 0;
            _paused = true;
            Generate();
        }

        static int Neighbour(int index, int direction)
        {
            switch (direction)
            {
                case 0: return index >= Size ? index - Size : -1;
                case 1: return index % Size < Size - 1 ? index + 1 : -1;
                case 2: return index < Count - Size ? index + Size : -1;
                default: return index % Size > 0 ? index - 1 : -1;
            }
        }

        static int Rotate(int mask) => ((mask << 1) | (mask >> 3)) & 15;

        void Generate()
        {
            System.Array.Clear(_nodes, 0, Count);
            _nodes[Source] = 10;
            _nodes[Source + 1] = 8;
            int visited = (1 << Source) | (1 << (Source + 1));
            int count = 1;
            _queue[0] = Source + 1;
            while (count > 0)
            {
                int current = _queue[count - 1];
                int start = _random.Next(4);
                bool advanced = false;
                for (int i = 0; i < 4; i++)
                {
                    int direction = (start + i) % 4;
                    int next = Neighbour(current, direction);
                    if (next < 0 || (visited & (1 << next)) != 0) continue;
                    _nodes[current] |= 1 << direction;
                    _nodes[next] |= 1 << ((direction + 2) % 4);
                    visited |= 1 << next;
                    _queue[count++] = next;
                    advanced = true;
                    break;
                }
                if (!advanced) count--;
            }
            _nodes[3] |= 2;
            _nodes[7] |= 2;
            _nodes[15] |= 2;
            for (int i = 0; i < Count; i++)
            {
                int turns = _random.Next(4);
                for (int turn = 0; turn < turns; turn++) _nodes[i] = Rotate(_nodes[i]);
            }
            _nodes[Source] = 5;
            _cooldown = .3f;
            Recalculate();
        }

        void Recalculate()
        {
            _powered = _connected = 0;
            if ((_nodes[Source] & 8) == 0) return;
            _powered = 1 << Source;
            int read = 0, count = 1;
            _queue[0] = Source;
            while (read < count)
            {
                int current = _queue[read++];
                for (int direction = 0; direction < 4; direction++)
                {
                    if ((_nodes[current] & (1 << direction)) == 0) continue;
                    int next = Neighbour(current, direction);
                    if (next < 0 || (_powered & (1 << next)) != 0 ||
                        (_nodes[next] & (1 << ((direction + 2) % 4))) == 0) continue;
                    _powered |= 1 << next;
                    _queue[count++] = next;
                }
            }
            if (Connected(3)) _connected++;
            if (Connected(7)) _connected++;
            if (Connected(15)) _connected++;
        }

        bool Connected(int index) => (_powered & (1 << index)) != 0 && (_nodes[index] & 2) != 0;

        public override void Update(bool paused)
        {
            _paused = paused;
            if (!paused) _cooldown = Mathf.Max(0, _cooldown - Mathf.Min(Time.unscaledDeltaTime, .05f));
        }

        void DrawNode(Rect tile, int index)
        {
            bool powered = (_powered & (1 << index)) != 0;
            var wire = powered ? new Color(.43f, .9f, .74f) : new Color(.28f, .4f, .46f);
            Widgets.DrawBoxSolid(new Rect(tile.x + 2, tile.y + 2, tile.width - 4, tile.height - 4),
                GUI.enabled && tile.Contains(Event.current.mousePosition) ? new Color(.13f, .23f, .26f) : new Color(.075f, .13f, .16f));
            float x = tile.center.x, y = tile.center.y, half = tile.width / 2;
            int mask = _nodes[index];
            if ((mask & 1) != 0) Widgets.DrawBoxSolid(new Rect(x - 2, tile.y, 4, half), wire);
            if ((mask & 2) != 0) Widgets.DrawBoxSolid(new Rect(x, y - 2, half, 4), wire);
            if ((mask & 4) != 0) Widgets.DrawBoxSolid(new Rect(x - 2, y, 4, half), wire);
            if ((mask & 8) != 0) Widgets.DrawBoxSolid(new Rect(tile.x, y - 2, half, 4), wire);
            Widgets.DrawBoxSolid(new Rect(x - 6, y - 6, 12, 12), wire);
            Widgets.DrawBoxSolid(new Rect(x - 3, y - 3, 6, 6), powered ? Color.white : new Color(.1f, .17f, .2f));
            if (GUI.enabled && Widgets.ButtonInvisible(tile))
            {
                _nodes[index] = Rotate(mask);
                Recalculate();
            }
        }

        public override void Draw(Rect rect)
        {
            var font = Text.Font;
            var anchor = Text.Anchor;
            var color = GUI.color;
            bool enabled = GUI.enabled;
            try
            {
                GUI.color = Color.white;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 40), Instructions);
                float side = Mathf.Min(rect.width - 64, rect.height - 142);
                var board = new Rect(rect.center.x - side / 2, rect.y + 42, side, side);
                Widgets.DrawBoxSolid(board, new Color(.025f, .05f, .065f));
                float cell = side / Size;
                GUI.enabled = enabled && !_paused && _cooldown <= 0;
                for (int i = 0; i < Count; i++)
                    DrawNode(new Rect(board.x + i % Size * cell, board.y + i / Size * cell, cell, cell), i);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.DrawBoxSolid(new Rect(board.x - 20, board.y + cell * 1.5f - 2, 20, 4), new Color(.43f, .9f, .74f));
                Widgets.Label(new Rect(board.x - 30, board.y + cell * 1.5f - 11, 14, 22), "+");
                for (int i = 0; i < 3; i++)
                {
                    int index = i == 2 ? 15 : 3 + i * Size;
                    float y = board.y + (index / Size + .5f) * cell;
                    var tint = Connected(index) ? new Color(.95f, .76f, .35f) : new Color(.25f, .32f, .36f);
                    Widgets.DrawBoxSolid(new Rect(board.xMax, y - 2, 12, 4), tint);
                    Widgets.DrawBoxSolid(new Rect(board.xMax + 12, y - 8, 12, 16), tint);
                }
                GUI.enabled = enabled;
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(rect.x, board.yMax + 2, rect.width, 25), "Rim3D_ResearchConnections".Translate(_connected));
                GUI.enabled = enabled && !_paused && _cooldown <= 0 && Reward > 0;
                if (Widgets.ButtonText(new Rect(rect.x, board.yMax + 31, rect.width * .65f - 4, 30),
                    "Rim3D_ResearchClaim".Translate(Reward)))
                {
                    _score += Reward;
                    Generate();
                }
                GUI.enabled = enabled && !_paused && _cooldown <= 0;
                if (Widgets.ButtonText(new Rect(rect.x + rect.width * .65f + 4, board.yMax + 31, rect.width * .35f - 4, 30),
                    "Rim3D_ResearchNew".Translate())) Generate();
                GUI.enabled = enabled;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x, board.yMax + 66, rect.width, 34), "Rim3D_ResearchReward".Translate());
            }
            finally
            {
                GUI.enabled = enabled;
                GUI.color = color;
                Text.Font = font;
                Text.Anchor = anchor;
            }
        }
    }
}
