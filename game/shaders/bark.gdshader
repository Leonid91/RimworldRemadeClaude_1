// Oak bark: furrowed procedural bark texture, wind sway matching the canopy, snow and wetness.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx, world_vertex_coords;

#include "res://shaders/world_common.gdshaderinc"

uniform sampler2D bark : source_color, filter_linear_mipmap, repeat_enable;

varying vec3 wpos;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz;
	float h = max(VERTEX.y - origin.y, 0.0);
	vec2 sway = wind_sway(origin, h * 0.12, 1.0) * (0.35 + COLOR.r * 0.65);
	VERTEX.xz += sway;
	wpos = VERTEX;
}

void fragment() {
	vec4 b = texture(bark, UV * vec2(1.0, 1.0));
	vec3 col = b.rgb * (0.8 + COLOR.g * 0.4);
	col *= 1.0 - wetness * 0.35;
	float up = clamp((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).y, 0.0, 1.0);
	col = mix(col, vec3(0.88, 0.9, 0.94), snow_cover * smoothstep(0.5, 0.9, up));
	col *= cloud_shadow(wpos.xz);
	ALBEDO = col;
	ROUGHNESS = mix(0.9, 0.6, wetness);
	NORMAL_MAP_DEPTH = 0.0;
	AO = mix(0.6, 1.0, b.a);
}
