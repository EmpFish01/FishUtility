// ── Static wrap table builder ──
// Builds a Gaussian-smoothed oversampled LUT from a ring buffer of Oklab colors.
// This is the compute-heavy LUT construction that runs on Rebuild/Reconfig.

use rayon::prelude::*;

/// Build a static wrap (smooth LUT) table from a ring of Oklab colors.
///
/// For each of `smooth_len` output samples, perform a Gaussian-weighted sum
/// of ring entries within `kernel_radius`. The ring wraps modulo `ring_len`.
///
/// # Parameters
/// * `ring_l/a/b` — Oklab ring buffer, length `ring_len`.
/// * `kernel_weights` — precomputed Gaussian weights, length `kernel_radius * 2 + 1`.
/// * `window_ring_len` — effective window size (same as `ring_len` in practice).
/// * `inv_oversample` — `window_ring_len / smooth_len`.
/// * `inv_sigma2x2` — `1 / (2 * sigma²)` for Gaussian kernel.
/// * `kernel_radius` — radius of kernel.
/// * `out_l/a/b` — output arrays, length `smooth_len`.
pub fn build_static_wrap_table(
    ring_l: &[f32],
    ring_a: &[f32],
    ring_b: &[f32],
    kernel_weights: &[f32],
    window_ring_len: usize,
    ring_len: usize,
    kernel_radius: usize,
    _smooth_len: usize,
    inv_oversample: f64,
    // inv_sigma2x2 is accepted but kernel_weights is precomputed — kept for API symmetry
    #[allow(unused_variables)] _inv_sigma2x2: f64,
    out_l: &mut [f32],
    out_a: &mut [f32],
    out_b: &mut [f32],
) {
    out_l.par_iter_mut()
        .zip(out_a.par_iter_mut())
        .zip(out_b.par_iter_mut())
        .enumerate()
        .for_each(|(s, ((lo, ao), bo))| {
            let center = s as f64 * inv_oversample;
            let i_center = center.round() as i32;
            let mut total_w = 0.0f32;
            let mut sum_l = 0.0f32;
            let mut sum_a = 0.0f32;
            let mut sum_b = 0.0f32;

            let kr = kernel_radius as i32;
            for d in -kr..=kr {
                let raw = i_center + d;
                let mut j = raw % (window_ring_len as i32);
                if j < 0 {
                    j += window_ring_len as i32;
                }
                if j as usize >= ring_len {
                    j %= ring_len as i32;
                }
                let k_idx = (d + kr) as usize;
                let w = kernel_weights[k_idx];
                if w > 0.0005 {
                    let ju = j as usize;
                    sum_l += ring_l[ju] * w;
                    sum_a += ring_a[ju] * w;
                    sum_b += ring_b[ju] * w;
                    total_w += w;
                }
            }

            if total_w > 0.0 {
                let inv = 1.0 / total_w;
                sum_l *= inv;
                sum_a *= inv;
                sum_b *= inv;
            }

            *lo = sum_l;
            *ao = sum_a;
            *bo = sum_b;
        });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_small_wrap_table() {
        // Simple 2-color ring: red (~0.6L, ~0.2A, ~0.0B) and blue
        // We'll use pre-converted Oklab values for red and blue
        let ring_l = [0.628, 0.452, 0.628, 0.452, 0.628, 0.452, 0.628, 0.452]; // alternating
        let ring_a = [0.225, -0.032, 0.225, -0.032, 0.225, -0.032, 0.225, -0.032];
        let ring_b = [0.040, -0.312, 0.040, -0.312, 0.040, -0.312, 0.040, -0.312];
        let ring_len = 8;
        let window_ring_len = 8;
        let smooth_len = 32;
        let kernel_radius = 4;
        let sigma2 = 0.042;
        let sigma_idx = (sigma2.sqrt() * (window_ring_len - 1) as f64).max(1.0);
        let inv_sigma2x2 = 1.0 / (2.0 * sigma_idx * sigma_idx);

        let mut kernel_weights = vec![0.0f32; kernel_radius * 2 + 1];
        for d in -(kernel_radius as i32)..=kernel_radius as i32 {
            let dist = d as f64;
            kernel_weights[(d + kernel_radius as i32) as usize] =
                (-dist * dist * inv_sigma2x2).exp() as f32;
        }

        let inv_oversample = window_ring_len as f64 / smooth_len as f64;
        let mut out_l = vec![0.0f32; smooth_len];
        let mut out_a = vec![0.0f32; smooth_len];
        let mut out_b = vec![0.0f32; smooth_len];

        build_static_wrap_table(
            &ring_l, &ring_a, &ring_b,
            &kernel_weights,
            window_ring_len, ring_len, kernel_radius, smooth_len,
            inv_oversample, inv_sigma2x2,
            &mut out_l, &mut out_a, &mut out_b,
        );

        // All outputs should be finite
        for i in 0..smooth_len {
            assert!(out_l[i].is_finite(), "L[{}] is not finite", i);
            assert!(out_a[i].is_finite(), "A[{}] is not finite", i);
            assert!(out_b[i].is_finite(), "B[{}] is not finite", i);
        }
    }
}
