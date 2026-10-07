// Leaf cards of oaks and bushes (look of the first prototype, RimworldTest): a painted leaf-cluster texture tinted per
// instance (INSTANCE_CUSTOM.rgb, sRGB; a = sway phase), canopy ambient occlusion in COLOR.r, normals spherized around
// the canopy for soft volume, back-lit translucency, wind sway growing with height, seasons (autumn hues, thinning
// in winter), and a dithered line-of-sight cut so the focused colonist stays visible under the canopy. Crowns are
// painted as distinct round clumps (old isometric strategy games): each clump has a warm, sunlit top and a deep,
// cool underside.
shader_type spatial;
render_mode world_vertex_coords, cull_disabled, diffuse_lambert_wrap, specular_schlick_ggx;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D leaf_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;
uniform float alpha_cut = 0.42;
uniform float sway_amount = 1.0;
uniform float translucency = 0.7;

varying vec3 sph_n;
varying vec3 tint;
varying float shade;
varying vec3 vorigin;
varying vec3 vworld;
varying float bare;
varying float vcloud; // the cloud shadow is constant over a plant: computed once per vertex

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	vorigin = origin;
	float ph = fract(INSTANCE_CUSTOM.a * 2.0) * 6.2831;
	VERTEX += tree_sway(noise_tex, VERTEX, origin, ph, sway_amount);
	// leaf flutter
	VERTEX += vec3(0.0, 1.0, 0.0) * sin(TIME * 5.0 + dot(VERTEX, vec3(3.1, 2.3, 1.7))) * 0.02 * (0.3 + wind_strength);
	sph_n = NORMAL;
	vworld = VERTEX;
	// seasons: per-tree drift towards gold / red through autumn, then the crown thins out in winter
	vec3 base = pow(INSTANCE_CUSTOM.rgb, vec3(2.2));
	vec3 autumn = pow(mix(vec3(0.85, 0.55, 0.12), vec3(0.75, 0.25, 0.08), fract(ph * 1.7)), vec3(2.2));
	float turn = autumn_amount() * (0.6 + 0.6 * fract(ph * 3.1));
	tint = mix(base, autumn, clamp(turn, 0.0, 1.0));
	bare = winter_amount();
	tint = mix(tint, pow(vec3(0.42, 0.30, 0.16), vec3(2.2)), bare * 0.8);
	shade = COLOR.r;
	vcloud = cloud_shadow(origin.xz) * 0.6 + 0.4;
}

void fragment() {
	vec4 t = texture(leaf_tex, UV);
	float cut = reveal_cut(vworld, CAMERA_POSITION_WORLD);
	// screen-door dither so the hole fades in softly
	float dither = fract(dot(FRAGCOORD.xy, vec2(0.5, 0.25)) * 2.0 + fract(FRAGCOORD.y * 0.5) * 0.5);
	ALPHA = cut > dither * 0.9 + 0.05 ? 0.0 : t.a * (1.0 - bare * 0.55);
	ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	vec3 c = t.rgb * tint * shade;
	// clump light: brighter, yellower tops, deeper undersides
	float top = clamp(sph_n.y * 0.5 + 0.5, 0.0, 1.0);
	c *= mix(0.72, 1.18, top);
	c = mix(c, c * vec3(1.18, 1.12, 0.72), smoothstep(0.65, 0.95, top) * 0.55);
	c = mix(c, vec3(0.6, 0.64, 0.71) * shade, snow_cover * smoothstep(0.3, 0.9, sph_n.y + 0.25) * 0.85);
	c *= vcloud;
	c *= mix(1.0, 0.8, wetness);
	ALBEDO = c;
	NORMAL = normalize((VIEW_MATRIX * vec4(sph_n, 0.0)).xyz);
	ROUGHNESS = mix(0.7, 0.4, wetness);
	SPECULAR = 0.12;
	BACKLIGHT = c * translucency;
}
