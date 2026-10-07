// Relief icons lying flat on the globe (one quad per hilly tile): atlas cell from INSTANCE_CUSTOM.r * 3.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, diffuse_burley, specular_disabled;

uniform sampler2D atlas : source_color, filter_linear_mipmap, repeat_disable;

varying float cell;

void vertex() {
	cell = floor(INSTANCE_CUSTOM.r * 3.0);
}

void fragment() {
	vec2 uv = vec2((UV.x + cell) / 3.0, UV.y);
	vec4 t = texture(atlas, uv);
	if (t.a < 0.02) discard;
	ALBEDO = t.rgb;
	ALPHA = t.a;
	ROUGHNESS = 0.9;
}
