using UnityEngine;
using Verse;

namespace Recovery.Rim3D
{
    abstract class ActivityMinigame : WorkMinigame
    {
        readonly System.Random _random = new System.Random();
        int _score;
        bool _paused = true;
        float _cooldown, _feedbackTime;
        string _feedback;
        protected System.Random Random => _random;
        protected bool CanPlay => !_paused && _cooldown <= 0;
        protected static Color Accent => new Color(.85f, .72f, .4f);
        protected abstract string Key { get; }
        protected virtual string RewardHint => "Rim3D_ActivityReward".Translate().ToString();
        public override string Title => ("Rim3D_Activity" + Key).Translate().ToString();
        public override string Instructions => ("Rim3D_Activity" + Key + "Hint").Translate().ToString();
        public override int PointsPerReward => 1;
        public override int Score => _score;

        public sealed override void Restart()
        {
            _score = 0;
            _paused = true;
            _feedback = null;
            _feedbackTime = 0;
            ResetRound();
            _cooldown = .35f;
        }

        protected abstract void ResetRound();
        protected abstract void DrawBoard(Rect board);
        protected virtual void Tick(float delta) { }
        protected virtual void CancelGesture() { }

        public sealed override void Update(bool paused)
        {
            _paused = paused;
            if (paused) { CancelGesture(); return; }
            float delta = Mathf.Min(Time.unscaledDeltaTime, .05f);
            _cooldown = Mathf.Max(0, _cooldown - delta);
            _feedbackTime = Mathf.Max(0, _feedbackTime - delta);
            if (CanPlay) Tick(delta);
        }

        protected void Complete(int points = 1)
        {
            if (!CanPlay) return;
            _score += points;
            _feedback = "Rim3D_ActivitySuccess".Translate(points).ToString();
            _feedbackTime = 1.3f;
            ResetRound();
            _cooldown = .45f;
        }

        protected void Mistake(bool reset = false)
        {
            if (!CanPlay) return;
            if (reset) ResetRound();
            _feedback = "Rim3D_ActivityMiss".Translate().ToString();
            _feedbackTime = 1.3f;
            _cooldown = .3f;
        }

        protected bool Button(Rect rect, string label)
        {
            bool active = CanPlay && GUI.enabled;
            Widgets.DrawBoxSolid(rect, active && rect.Contains(Event.current.mousePosition) ? Accent : new Color(.28f, .36f, .38f));
            Widgets.DrawBoxSolid(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2), new Color(.075f, .12f, .14f));
            Widgets.Label(rect, label);
            return active && Widgets.ButtonInvisible(rect);
        }

        protected static void Bar(Rect rect, float fill, Color tint)
        {
            Widgets.DrawBoxSolid(rect, new Color(.07f, .1f, .12f));
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill), rect.height), tint);
        }

        protected static void Stroke(Vector2 start, Vector2 end, Color tint, float width = 3)
        {
            var matrix = GUI.matrix;
            try
            {
                var delta = end - start;
                GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
                Widgets.DrawBoxSolid(new Rect(start.x, start.y - width / 2, delta.magnitude, width), tint);
            }
            finally { GUI.matrix = matrix; }
        }

        protected static Vector2 Point(Rect rect, Vector2 normalized) =>
            new Vector2(rect.x + rect.width * normalized.x, rect.y + rect.height * normalized.y);

        public sealed override void Draw(Rect rect)
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
                Widgets.Label(new Rect(rect.x, rect.y, rect.width, 42), Instructions);
                var board = new Rect(rect.x, rect.y + 46, rect.width, rect.height - 146);
                Widgets.DrawBoxSolid(board, new Color(.035f, .065f, .08f));
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                DrawBoard(board);
                Widgets.Label(new Rect(rect.x, board.yMax + 4, rect.width, 28), _feedbackTime > 0 ? _feedback :
                    "Rim3D_ActivityReady".Translate().ToString());
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rect.x, board.yMax + 36, rect.width, 56), RewardHint);
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
