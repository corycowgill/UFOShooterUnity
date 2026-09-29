// Browser facts Unity cannot see from managed code.
//
// Application.isMobilePlatform is false in WebGL no matter what the build is running on, and the
// user-agent string is the wrong question anyway - what the game needs to know is whether the
// player has a finger or a mouse.
mergeInto(LibraryManager.library, {

  // 1 when the PRIMARY pointer is coarse - a finger, not a mouse.
  //
  // '(pointer: coarse)' is the vendor-neutral answer and is what a phone reports. maxTouchPoints
  // is checked as well because a few desktop browsers with a touchscreen attached answer the media
  // query coarse while still being driven by a mouse; requiring both keeps a touchscreen laptop on
  // the keyboard-and-mouse path, which is what its owner expects.
  UFO_IsTouchPrimary: function () {
    try {
      var coarse = !!(window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
      var fine = !!(window.matchMedia && window.matchMedia('(any-pointer: fine)').matches);
      var points = navigator.maxTouchPoints || 0;
      return (coarse && points > 0 && !fine) ? 1 : 0;
    } catch (e) { return 0; }
  },

  // iOS/iPadOS, which needs handling the others do not: no pointer lock, no Fullscreen API on
  // iPhone, and an audio context that stays suspended until a real touch.
  //
  // iPadOS 13+ deliberately reports itself as 'Macintosh', so the UA alone cannot answer this;
  // a Mac reports maxTouchPoints 0, an iPad reports 5.
  UFO_IsIOS: function () {
    try {
      var ua = navigator.userAgent || '';
      if (/iPad|iPhone|iPod/.test(ua)) return 1;
      return (/Macintosh/.test(ua) && (navigator.maxTouchPoints || 0) > 1) ? 1 : 0;
    } catch (e) { return 0; }
  },

  // True while the viewport is taller than it is wide. The game is unplayable in portrait, so the
  // page puts up a rotate prompt rather than letting the player fight a letterboxed strip.
  UFO_IsPortrait: function () {
    try { return (window.innerHeight > window.innerWidth) ? 1 : 0; } catch (e) { return 0; }
  },
});
