const preview = document.querySelector('#preview');
const photo = preview.querySelector('img');
const caption = preview.querySelector('#preview-caption');
const closeButton = preview.querySelector('button');

for (const link of document.querySelectorAll('[data-screenshot]')) {
  link.addEventListener('click', (event) => {
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    const thumbnail = link.querySelector('img');
    photo.src = link.href;
    const description = thumbnail?.alt ?? link.textContent.trim();
    photo.alt = description;
    caption.textContent = description;
    preview.showModal();
    closeButton.focus();
  });
}

closeButton.addEventListener('click', () => preview.close());
preview.addEventListener('click', (event) => {
  if (event.target === preview) preview.close();
});

const carousel = document.querySelector('[data-carousel]');
const slides = [...carousel.querySelectorAll('.feature-slide')];
const indicatorGroup = carousel.querySelector('.feature-indicators');
const indicators = [...indicatorGroup.querySelectorAll('button')];
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
let slideIndex = 0;
let rotationTimer;

function startRotation() {
  window.clearTimeout(rotationTimer);
  if (reducedMotion.matches || document.hidden || preview.open || carousel.querySelector(':focus-visible')) return;
  rotationTimer = window.setTimeout(() => {
    if (!preview.open) showSlide((slideIndex + 1) % slides.length);
  }, 5000);
}

function showSlide(index) {
  slides[slideIndex].hidden = true;
  indicators[slideIndex].removeAttribute('aria-current');
  slideIndex = index;
  slides[slideIndex].hidden = false;
  indicators[slideIndex].setAttribute('aria-current', 'true');
  startRotation();
}

indicators.forEach((indicator, index) => {
  indicator.addEventListener('click', () => showSlide(index));
});
reducedMotion.addEventListener('change', startRotation);
document.addEventListener('visibilitychange', startRotation);
carousel.addEventListener('focusin', startRotation);
carousel.addEventListener('focusout', () => queueMicrotask(startRotation));
carousel.addEventListener('click', startRotation);
preview.addEventListener('close', startRotation);
indicatorGroup.hidden = false;
startRotation();

const versionButton = document.querySelector('.version-share .version');
const downloadQr = document.querySelector('#download-qr');
const qrAvailable = window.matchMedia('(min-width: 526px) and (hover: hover)');

function setQrVisible(visible) {
  visible = visible && qrAvailable.matches;
  downloadQr.hidden = !visible;
  versionButton.setAttribute('aria-expanded', String(visible));
}

function syncQrAvailability() {
  versionButton.disabled = !qrAvailable.matches;
  setQrVisible(false);
}

versionButton.addEventListener('pointerenter', (event) => {
  if (event.pointerType === 'mouse') setQrVisible(true);
});
versionButton.addEventListener('pointerleave', (event) => {
  if (event.pointerType === 'mouse') setQrVisible(false);
});
versionButton.addEventListener('click', (event) => {
  if (event.detail === 0) setQrVisible(downloadQr.hidden);
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !downloadQr.hidden && !preview.open) setQrVisible(false);
});

qrAvailable.addEventListener('change', syncQrAvailability);
syncQrAvailability();
