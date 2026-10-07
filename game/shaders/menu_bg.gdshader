// Full-screen backdrop for the set-up screens: deep space gradient, slow nebula drift, twinkling stars, vignette.
shader_type canvas_item;

uniform vec3 tint_a : source_color = vec3(0.03, 0.06, 0.09);
uniform vec3 tint_b : source_color = vec3(0.10, 0.05, 0.10);

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}
float fbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 5; i++) { s += a * noise(p); p *= 2.03; a *= 0.5; } return s; }

void fragment() {
	vec2 uv = UV;
	vec2 p = uv * vec2(16.0 / 9.0, 1.0);
	float t = TIME * 0.01;
	float n = fbm(p * 2.2 + vec2(t, -t * 0.6));
	float n2 = fbm(p * 4.0 - vec2(t * 0.8, t));
	vec3 col = mix(vec3(0.008, 0.012, 0.02), tint_a, smoothstep(0.35, 0.85, n));
	col += tint_b * smoothstep(0.55, 0.9, n2) * 0.6;
	// stars
	vec2 g = p * 140.0;
	vec2 cell = floor(g);
	float h = hash(cell);
	if (h > 0.985) {
		vec2 o = vec2(hash(cell + 3.1), hash(cell + 7.7));
		float d = length(fract(g) - o);
		float tw = 0.6 + 0.4 * sin(TIME * (1.0 + h * 5.0) + h * 50.0);
		col += vec3(0.85, 0.9, 1.0) * smoothstep(0.12, 0.0, d) * tw * (h - 0.985) * 60.0;
	}
	// vignette
	float v = smoothstep(1.25, 0.35, length(uv - vec2(0.5, 0.45)) * 1.4);
	COLOR = vec4(col * v, 1.0);
}
