import { expect } from '@playwright/test';

export const cropColors = [[240, 40, 40], [40, 200, 40], [40, 40, 240], [240, 200, 40]];

// Inspect rendered pixels, not just inline styles: this also catches global CSS
// overriding the crop, incorrectly stretching it, or displaying the original.
export async function expectCropThumbnail(page, row, ratio, colors) {
  const thumbnail = row.locator('.crop-thumbnail');
  const region = thumbnail.locator('.crop-thumbnail-region');
  await expect(region).toBeVisible();
  // Center it within the independently scrolling pane, clear of sticky tools.
  await region.evaluate(el => el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' }));
  await expect.poll(() => region.locator('img').evaluate(img => img.complete && img.naturalWidth > 0)).toBe(true);
  await expect.poll(async () => {
    const box = await region.boundingBox(); return box.width / box.height;
  }).toBeCloseTo(ratio, 2);
  const frame = await thumbnail.boundingBox(), box = await region.boundingBox();
  expect(Math.min(frame.width - box.width, frame.height - box.height)).toBeCloseTo(0, 1);
  expect(box.x - frame.x).toBeCloseTo((frame.width - box.width) / 2, 1);
  expect(box.y - frame.y).toBeCloseTo((frame.height - box.height) / 2, 1);
  const image = await region.locator('img').boundingBox();
  const intrinsicRatio = await region.locator('img').evaluate(img => img.naturalWidth / img.naturalHeight);
  expect(image.width / image.height).toBeCloseTo(intrinsicRatio, 2);
  // Pending requests disable the containing button. Remove only that visual
  // fade while checking crop pixels; geometry and clipping remain unchanged.
  const screenshot = await region.screenshot({ style: '.compact-image-input:disabled { opacity: 1 !important; }' });
  const pixels = await page.evaluate(async bytes => {
    const bitmap = await createImageBitmap(new Blob([new Uint8Array(bytes)], { type: 'image/png' }));
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height), context = canvas.getContext('2d');
    context.drawImage(bitmap, 0, 0); bitmap.close();
    return [[.25, .25], [.75, .25], [.25, .75], [.75, .75]].map(([x, y]) =>
      [...context.getImageData(Math.floor(canvas.width * x), Math.floor(canvas.height * y), 1, 1).data].slice(0, 3));
  }, [...screenshot]);
  expect(pixels).toEqual(colors);
}
