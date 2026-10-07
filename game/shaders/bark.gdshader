// Oak bark (first prototype's look): two browns mixed by stretched noise, darker at the root, sways with the canopy,
// snow on top, wet and cloud-shadowed. UV.y runs along the trunk and limbs.
shader_type spatial;
render_mode world_vertex_coords, diffuse_burley;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;
uniform vec3 col_a : source_color = vec3(0.24, 0.18, 0.12);
uniform vec3 col_b : source_color = vec3(0.42, 0.33, 0.24);
uniform float sway_amount = 1.0;

varying vec3 vorigin;
varying float vup;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	vorigin = origin;
	VERTEX += tree_sway(noise_tex, VERTEX, origin, fract(INSTANCE_CUSTOM.a * 2.0) * 6.2831, sway_amount);
	vup = NORMAL.y;
}

void fragment() {
	float s = texture(noise_tex, vec2(UV.x * 1.5, UV.y * 0.12)).r;
	float s2 = texture(noise_tex, vec2(UV.x * 4.0, UV.y * 0.5)).b;
	vec3 c = mix(col_a, col_b, s * 0.7 + s2 * 0.3);
	c *= mix(0.6, 1.0, smoothstep(0.0, 0.7, UV.y));
	c = mix(c, vec3(0.6, 0.64, 0.71), snow_cover * smoothstep(0.5, 0.9, vup) * 0.8);
	c *= cloud_shadow(vorigin.xz) * 0.6 + 0.4;
	c *= mix(1.0, 0.7, wetness);
	ALBEDO = c;
	ROUGHNESS = 0.9;
}
