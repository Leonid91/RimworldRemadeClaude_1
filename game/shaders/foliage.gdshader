// Oak canopy leaf cards: alpha-scissored leaf clusters with spherized normals for soft volume, back-lit translucency,
// wind sway weighted by vertex colour (r = branch flexibility), seasonal colours, snow on top, and a dithered
// cut-out around the colonist under your control so they stay visible under the trees.
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx, world_vertex_coords;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D leaves : source_color, filter_linear_mipmap, repeat_disable;
uniform float alpha_cut = 0.42;

varying vec3 wpos;
varying float variation;
varying float canopy_h;

void vertex() {
	// world_vertex_coords: VERTEX is already in world space
	vec3 origin = MODEL_MATRIX[3].xyz;
	variation = fract(origin.x * 0.731 + origin.z * 0.317);
	float h = max(VERTEX.y - origin.y, 0.0);
	canopy_h = h;
	vec2 sway = wind_sway(origin, h * 0.12, 1.0) * (0.35 + COLOR.r * 0.65);
	// leaf flutter
	sway += vec2(sin(TIME * 4.3 + VERTEX.x * 2.1 + VERTEX.y), cos(TIME * 3.7 + VERTEX.z * 1.9)) * 0.025 * wind_strength * COLOR.r;
	VERTEX.xz += sway;
	wpos = VERTEX;
}

void fragment() {
	vec4 tex = texture(leaves, UV);
	if (tex.a < alpha_cut) discard;
	// reveal: dither leaves away around the controlled pawn
	if (reveal_pos.w > 0.0) {
		float d = length(wpos.xz - reveal_pos.xz);
		float above = smoothstep(reveal_pos.y + 0.5, reveal_pos.y + 2.0, wpos.y);
		float fade = smoothstep(reveal_pos.w, reveal_pos.w * 0.45, d) * above;
		float dither = fract(dot(FRAGCOORD.xy, vec2(0.5, 0.25)) + hash12(floor(FRAGCOORD.xy)) * 0.5);
		if (fade > dither) discard;
	}
	vec3 base = season_foliage(vec3(0.20, 0.32, 0.10) * (0.85 + variation * 0.3), variation);
	vec3 col = tex.rgb / max(vec3(0.23, 0.36, 0.12), vec3(0.01)) * base;
	float up = clamp(NORMAL.y, 0.0, 1.0);
	col = mix(col, vec3(0.9, 0.92, 0.95), snow_cover * smoothstep(0.3, 0.8, (INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y));
	col *= cloud_shadow(wpos.xz);
	ALBEDO = col;
	ROUGHNESS = 0.75;
	SPECULAR = 0.25;
	BACKLIGHT = col * 0.6;
	AO = mix(0.55, 1.0, clamp(canopy_h / 6.0, 0.0, 1.0));
	AO_LIGHT_AFFECT = 0.4;
}
