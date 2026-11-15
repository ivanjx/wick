// Counts the calls each named interval made, so tests can tell "not called" from "called but not rendered".
window.wickE2E = {
    calls: {},

    startInterval(dotNetRef, name, milliseconds) {
        this.calls[name] = 0;

        const id = setInterval(() => {
            this.calls[name]++;

            // Rejects once the reference is disposed with the component.
            dotNetRef.invokeMethodAsync("Tick").catch(() => clearInterval(id));
        }, milliseconds);
    },
};
