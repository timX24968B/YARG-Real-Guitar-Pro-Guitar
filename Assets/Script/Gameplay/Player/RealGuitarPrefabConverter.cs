using System;
using System.Reflection;
using UnityEngine;
using YARG.Gameplay.Visuals;
using YARG.Player;

namespace YARG.Gameplay.Player
{
    /// <summary>
    /// Reuses the existing five-fret highway prefab as generic TrackPlayer
    /// scaffolding while replacing its player and note components with the
    /// Real Guitar implementations. No six-fret assets or behaviour are used.
    /// </summary>
    public static class RealGuitarPrefabConverter
    {
        private const BindingFlags FIELD_FLAGS =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        public static RealGuitarPlayer ConvertPlayer(GameObject playerObject, YargPlayer player)
        {
            var fiveFret = playerObject.GetComponent<FiveFretGuitarPlayer>();
            if (fiveFret == null)
            {
                return playerObject.GetComponent<RealGuitarPlayer>();
            }

            var realGuitar = playerObject.AddComponent<RealGuitarPlayer>();
            CopyFields(fiveFret, realGuitar);

            // The old component has not been initialized, so its Player field is
            // null. OnDestroy -> GameplayDestroy dereferences it, so provide the
            // real player before removing the component.
            SetPlayer(fiveFret, player);

            // Both components are TrackPlayers, so the old one must be removed
            // immediately rather than at the end of the frame.
            UnityEngine.Object.DestroyImmediate(fiveFret);
            return realGuitar;
        }

        public static GameObject ConvertNotePrefab(GameObject notePrefab)
        {
            var fiveFret = notePrefab.GetComponent<FiveFretGuitarNoteElement>();
            if (fiveFret == null)
            {
                return notePrefab;
            }

            var realGuitar = notePrefab.AddComponent<RealGuitarNoteElement>();
            CopyFields(fiveFret, realGuitar);

            UnityEngine.Object.DestroyImmediate(fiveFret);
            return notePrefab;
        }

        private static void SetPlayer(BasePlayer trackPlayer, YargPlayer player)
        {
            var field = typeof(BasePlayer).GetField("<Player>k__BackingField", FIELD_FLAGS);
            field?.SetValue(trackPlayer, player);
        }

        private static void CopyFields(object source, object target)
        {
            var type = source.GetType();
            while (type != null && type != typeof(MonoBehaviour))
            {
                foreach (var field in type.GetFields(FIELD_FLAGS))
                {
                    if (field.IsInitOnly || field.IsLiteral)
                    {
                        continue;
                    }

                    var targetField = FindField(target.GetType(), field.Name);
                    if (targetField == null || targetField.IsInitOnly || targetField.IsLiteral)
                    {
                        continue;
                    }

                    if (!targetField.FieldType.IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }

                    targetField.SetValue(target, field.GetValue(source));
                }

                type = type.BaseType;
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                var field = type.GetField(name, FIELD_FLAGS);
                if (field != null)
                {
                    return field;
                }

                type = type.BaseType;
            }

            return null;
        }
    }
}
