using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ThePitt.UI
{
    /// <summary>
    /// Calls back when the pointer moves over this. Unlike an EventTrigger it handles nothing else, so scroll
    /// and drag events still reach a ScrollRect behind it.
    /// </summary>
    public class HoverRelay : MonoBehaviour, IPointerEnterHandler
    {
        public Action Entered;

        public void OnPointerEnter(PointerEventData eventData) => Entered?.Invoke();
    }
}
