using System.Collections.Generic;
using TMPro;
using UnityEngine;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Gameplay.Player;
using YARG.Helpers.Extensions;
using YARG.Themes;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// A Real Guitar note on the six-string highway. The note is positioned by
    /// string, colored with the string's Rocksmith-style color, and labelled
    /// with its fret number.
    /// </summary>
    public sealed class RealGuitarNoteElement : NoteElement<ProGuitarNote, RealGuitarPlayer>
    {
        [Space]
        [SerializeField]
        private SustainLine _normalSustainLine;
        [SerializeField]
        private SustainLine _openSustainLine;
        [SerializeField]
        private SustainLine _wildcardSustainLine;

        private SustainLine _sustainLine;
        private TextMeshPro _fretText;
        private SpriteRenderer _fretBadge;
        private Transform _fretLabelRoot;

        private static Sprite _badgeSprite;

        protected override float RemovePointOffset => (float) NoteRef.TimeLength * Player.NoteSpeed;

        public override void SetThemeModels(Dictionary<ThemeNoteType, GameObject> models,
            Dictionary<ThemeNoteType, GameObject> starPowerModels)
        {
            // The five-fret note prefab normally has these groups built before
            // RealGuitarPrefabConverter swaps in this component.
            if (NoteGroups == null || NoteGroups.Length == 0)
            {
                CreateNoteGroupArrays(3);
                AssignNoteGroup(models, starPowerModels, 0, ThemeNoteType.Normal);
                AssignNoteGroup(models, starPowerModels, 1, ThemeNoteType.HOPO);
                AssignNoteGroup(models, starPowerModels, 2, ThemeNoteType.Tap);
            }
        }

        protected override void InitializeElement()
        {
            base.InitializeElement();

            var noteGroups = IsStarPowerVisible ? StarPowerNoteGroups : NoteGroups;
            int groupIndex = NoteRef.Type switch
            {
                ProGuitarNoteType.Hopo => 1,
                ProGuitarNoteType.Tap  => 2,
                _                      => 0
            };

            NoteGroup = noteGroups[groupIndex];
            NoteGroup.SetActive(true);
            NoteGroup.Initialize();

            int lane = Player.GetLanePosition(NoteRef.String);
            transform.localPosition = new Vector3(GetElementX(lane, Player.LaneCount), 0f, 0f);

            if (_openSustainLine != null)
            {
                _openSustainLine.gameObject.SetActive(false);
            }
            if (_wildcardSustainLine != null)
            {
                _wildcardSustainLine.gameObject.SetActive(false);
            }

            _sustainLine = _normalSustainLine;
            if (NoteRef.IsSustain && _sustainLine != null)
            {
                _sustainLine.gameObject.SetActive(true);
                float len = (float) NoteRef.TimeLength * Player.NoteSpeed;
                _sustainLine.Initialize(len);
            }

            CreateFretLabel();
            UpdateColor();
        }

        public override void HitNote()
        {
            base.HitNote();

            if (NoteRef.IsSustain)
            {
                HideNotes();
            }
            else
            {
                ParentPool.Return(this);
            }
        }

        public override void MissNote()
        {
            base.MissNote();
            UpdateColor();
        }

        protected override void UpdateElement()
        {
            base.UpdateElement();
            _sustainLine?.UpdateSustainLine();
            UpdateFretLabelOrientation();
        }

        protected override void OnNoteStateChanged()
        {
            base.OnNoteStateChanged();
            UpdateColor();
        }

        public override void OnStarPowerUpdated()
        {
            base.OnStarPowerUpdated();
            UpdateColor();
        }

        protected override bool CalcStarPowerVisible()
        {
            if (!NoteRef.IsStarPower)
            {
                return false;
            }

            return !(((GuitarEngineParameters) Player.BaseParameters).NoStarPowerOverlap
                && Player.BaseStats.IsStarPowerActive);
        }

        private void CreateFretLabel()
        {
            if (_fretText == null)
            {
                var rootObject = new GameObject("Fret Number Root");
                rootObject.transform.SetParent(transform, false);
                rootObject.transform.localPosition = new Vector3(0f, 0.2f, 0f);
                _fretLabelRoot = rootObject.transform;

                var badgeObject = new GameObject("Fret Number Badge");
                badgeObject.transform.SetParent(_fretLabelRoot, false);
                badgeObject.transform.localPosition = Vector3.zero;
                badgeObject.transform.localRotation = Quaternion.identity;
                badgeObject.transform.localScale = Vector3.one * 0.42f;

                _fretBadge = badgeObject.AddComponent<SpriteRenderer>();
                _fretBadge.sprite = GetBadgeSprite();
                _fretBadge.color = new Color(0f, 0f, 0f, 0.78f);
                _fretBadge.sortingOrder = 10;

                var labelObject = new GameObject("Fret Number");
                labelObject.transform.SetParent(_fretLabelRoot, false);
                labelObject.transform.localPosition = new Vector3(0f, 0f, -0.001f);
                labelObject.transform.localRotation = Quaternion.identity;
                labelObject.transform.localScale = Vector3.one * 0.3f;

                _fretText = labelObject.AddComponent<TextMeshPro>();
                _fretText.alignment = TextAlignmentOptions.Center;
                _fretText.fontSize = 6f;
                _fretText.fontStyle = FontStyles.Bold;
                _fretText.textWrappingMode = TextWrappingModes.NoWrap;
                _fretText.color = Color.white;
                _fretText.outlineColor = Color.black;
                _fretText.outlineWidth = 0.4f;
                _fretText.sortingOrder = 11;
                _fretText.rectTransform.sizeDelta = new Vector2(2f, 2f);

                if (TMP_Settings.defaultFontAsset != null)
                {
                    _fretText.font = TMP_Settings.defaultFontAsset;
                }
            }

            _fretBadge.gameObject.SetActive(true);
            _fretText.gameObject.SetActive(true);
            _fretText.text = NoteRef.Fret.ToString();
            _fretText.ForceMeshUpdate();
            UpdateFretLabelOrientation();
        }

        private void UpdateFretLabelOrientation()
        {
            if (_fretLabelRoot == null)
            {
                return;
            }

            var camera = Player.TrackCamera;
            if (camera == null)
            {
                return;
            }

            Vector3 toCamera = camera.transform.position - _fretLabelRoot.position;
            _fretLabelRoot.rotation = Quaternion.LookRotation(toCamera, camera.transform.up) *
                Quaternion.Euler(0f, 180f, 0f);
        }

        private static Sprite GetBadgeSprite()
        {
            if (_badgeSprite != null)
            {
                return _badgeSprite;
            }

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "RealGuitarFretBadge",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[size * size];
            const float center = (size - 1) * 0.5f;
            const float half = size * 0.5f - 1f;
            const float radius = 12f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x - center);
                    float dy = Mathf.Abs(y - center);
                    float cornerX = Mathf.Max(dx - (half - radius), 0f);
                    float cornerY = Mathf.Max(dy - (half - radius), 0f);
                    float outside = Mathf.Sqrt(cornerX * cornerX + cornerY * cornerY);
                    float alpha = outside <= radius ? 1f : 0f;

                    // Soft one-pixel edge.
                    if (alpha == 0f && outside <= radius + 1f)
                    {
                        alpha = radius + 1f - outside;
                    }

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte) (alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            _badgeSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), size);
            return _badgeSprite;
        }

        private void UpdateColor()
        {
            var colors = Player.Player.ColorProfile.RealGuitar;
            var color = IsStarPowerVisible
                ? colors.GetNoteStarPowerColor(NoteRef.String)
                : colors.GetNoteColor(NoteRef.String);

            if (NoteRef.WasMissed)
            {
                color = colors.Miss;
            }

            if (!NoteRef.WasHit && NoteGroup != null)
            {
                NoteGroup.SetColorWithEmission(color.ToUnityColor(), color.ToUnityColor());
                NoteGroup.SetMetalColor(colors.GetMetalColor(IsStarPowerVisible).ToUnityColor());
            }

            if (_sustainLine != null && NoteRef.IsSustain)
            {
                _sustainLine.SetState(SustainState, color.ToUnityColor());
            }
        }

        protected override void HideElement()
        {
            HideNotes();

            if (_normalSustainLine != null)
            {
                _normalSustainLine.gameObject.SetActive(false);
            }
            if (_openSustainLine != null)
            {
                _openSustainLine.gameObject.SetActive(false);
            }
            if (_wildcardSustainLine != null)
            {
                _wildcardSustainLine.gameObject.SetActive(false);
            }
            if (_fretText != null)
            {
                _fretText.gameObject.SetActive(false);
            }
            if (_fretBadge != null)
            {
                _fretBadge.gameObject.SetActive(false);
            }
        }
    }
}
