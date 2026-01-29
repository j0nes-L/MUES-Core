#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace MUES.Editor
{
    [InitializeOnLoad]
    public static class MUES_LayerSetup
    {
        // Definierte Layer mit ihren gewünschten Slot-Positionen
        private static readonly Dictionary<string, int> DesiredLayerSlots = new Dictionary<string, int>
        {
            { "MUES_RenderWhileLoading", 18 },
            { "MUES_RoomGeometry", 19 },
            { "MUES_Floor", 20 },
            { "MUES_Wall", 21 },
            { "MUES_Head", 22 }
        };

        static MUES_LayerSetup()
        {
            foreach (var layerEntry in DesiredLayerSlots)
            {
                EnsureLayerAtSlot(layerEntry.Key, layerEntry.Value);
            }
        }

        private static void EnsureLayerAtSlot(string layerName, int desiredSlot)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[MUES_LayerSetup] TagManager.asset not found.");
                return;
            }

            var tagManager = new SerializedObject(assets[0]);
            var layersProp = tagManager.FindProperty("layers");

            // Prüfen ob der Layer bereits existiert
            int existingIndex = -1;
            for (int i = 0; i < layersProp.arraySize; i++)
            {
                var sp = layersProp.GetArrayElementAtIndex(i);
                if (sp != null && sp.stringValue == layerName)
                {
                    existingIndex = i;
                    break;
                }
            }

            // Layer existiert bereits am gewünschten Slot
            if (existingIndex == desiredSlot)
                return;

            // Layer existiert an einem anderen Slot - entfernen
            if (existingIndex >= 0)
            {
                var existingSp = layersProp.GetArrayElementAtIndex(existingIndex);
                existingSp.stringValue = "";
                tagManager.ApplyModifiedProperties();
            }

            // Prüfen ob der gewünschte Slot verfügbar ist
            var desiredSlotProp = layersProp.GetArrayElementAtIndex(desiredSlot);
            string existingLayerAtSlot = desiredSlotProp?.stringValue;

            if (!string.IsNullOrEmpty(existingLayerAtSlot) && existingLayerAtSlot != layerName)
            {
                // Der Slot ist belegt - verschiebe den vorhandenen Layer
                int newSlot = FindFreeSlot(layersProp, desiredSlot);
                if (newSlot >= 0)
                {
                    // Objekte mit dem alten Layer aktualisieren
                    int oldLayerMask = desiredSlot;
                    int newLayerMask = newSlot;
                    
                    // Verschiebe den existierenden Layer auf den neuen Slot
                    var newSlotProp = layersProp.GetArrayElementAtIndex(newSlot);
                    newSlotProp.stringValue = existingLayerAtSlot;
                    tagManager.ApplyModifiedProperties();
                    
                    // Aktualisiere alle Objekte die den alten Layer verwenden
                    UpdateObjectsWithLayer(oldLayerMask, newLayerMask);
                    
                    Debug.Log($"[MUES_LayerSetup] Layer '{existingLayerAtSlot}' moved from slot {desiredSlot} to slot {newSlot}.");
                }
                else
                {
                    ConsoleMessage.Send(true, $"[MUES_LayerSetup] No free slot to move existing layer '{existingLayerAtSlot}'. Cannot set '{layerName}' at slot {desiredSlot}.", Color.red);
                    return;
                }
            }

            // Setze den MUES Layer auf den gewünschten Slot
            desiredSlotProp.stringValue = layerName;
            tagManager.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            Debug.Log($"[MUES_LayerSetup] Layer '{layerName}' set at slot {desiredSlot}.");
        }

        private static int FindFreeSlot(SerializedProperty layersProp, int excludeSlot)
        {
            // Suche nach einem freien Slot ab Layer 8 (User Layers)
            for (int i = 8; i < layersProp.arraySize; i++)
            {
                if (i == excludeSlot)
                    continue;
                    
                // Überspringe Slots die für MUES reserviert sind
                bool isReserved = false;
                foreach (var entry in DesiredLayerSlots)
                {
                    if (entry.Value == i)
                    {
                        isReserved = true;
                        break;
                    }
                }
                if (isReserved)
                    continue;

                var sp = layersProp.GetArrayElementAtIndex(i);
                if (sp != null && string.IsNullOrEmpty(sp.stringValue))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void UpdateObjectsWithLayer(int oldLayer, int newLayer)
        {
            // Finde alle GameObjects in der Szene und in Prefabs
            GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            int updatedCount = 0;

            foreach (var obj in allObjects)
            {
                if (obj.layer == oldLayer)
                {
                    // Prüfen ob es ein Prefab ist
                    if (PrefabUtility.IsPartOfPrefabAsset(obj))
                    {
                        string prefabPath = AssetDatabase.GetAssetPath(obj);
                        if (!string.IsNullOrEmpty(prefabPath))
                        {
                            // Prefab bearbeiten
                            using (var editingScope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
                            {
                                UpdateLayerRecursive(editingScope.prefabContentsRoot, oldLayer, newLayer);
                            }
                        }
                    }
                    else if (!EditorUtility.IsPersistent(obj))
                    {
                        // Szenen-Objekt
                        Undo.RecordObject(obj, "Update Layer");
                        obj.layer = newLayer;
                        EditorUtility.SetDirty(obj);
                        updatedCount++;
                    }
                }
            }

            if (updatedCount > 0)
            {
                Debug.Log($"[MUES_LayerSetup] Updated {updatedCount} objects from layer {oldLayer} to layer {newLayer}.");
            }
        }

        private static void UpdateLayerRecursive(GameObject root, int oldLayer, int newLayer)
        {
            if (root.layer == oldLayer)
            {
                root.layer = newLayer;
            }

            foreach (Transform child in root.transform)
            {
                UpdateLayerRecursive(child.gameObject, oldLayer, newLayer);
            }
        }
    }
}
#endif
