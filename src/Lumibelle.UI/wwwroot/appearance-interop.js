export function attach(dotnet) {
    const changed = event => dotnet.invokeMethodAsync('AppearanceChanged', event.detail).catch(() => {});
    window.addEventListener('lumibelle:appearance', changed);
    return {
        snapshot: () => window.lumibelleAppearance.snapshot(),
        set: value => window.lumibelleAppearance.set(value),
        dispose: () => window.removeEventListener('lumibelle:appearance', changed)
    };
}
