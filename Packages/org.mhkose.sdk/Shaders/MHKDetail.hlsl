#ifndef MHK_DETAIL_INCLUDED
#define MHK_DETAIL_INCLUDED

// Detail cut of the graphics tier, a global set by MHK.Core.GraphicsQuality: 0 = full (High, and unset outside the game),
// 1 = Medium, 2 = Low. Shaders branch on it (uniform branches) instead of keywords, so no tier adds shader variants.
float _MHKDetailCut;

#define MHK_DETAIL_REDUCED (_MHKDetailCut > 0.5)  // Medium and Low
#define MHK_DETAIL_LOW (_MHKDetailCut > 1.5)

#endif
