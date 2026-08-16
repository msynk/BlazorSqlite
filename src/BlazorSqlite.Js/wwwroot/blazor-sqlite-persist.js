export function requestPersistence() {
  return typeof navigator.storage?.persist === 'function'
    ? navigator.storage.persist()
    : false;
}
