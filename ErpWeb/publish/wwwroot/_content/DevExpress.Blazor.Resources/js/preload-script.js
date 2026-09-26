if(!window['DxAttributeWatcher']) {
    class DxCustomAttribute {
        static #items = [];
        static register(attribute) {
            this.#items.push(attribute);
        }
        static get attributes() {
            return [...this.#items];
        }

        allowProcessAttributeRemoved(element, attrName) {
            return this.attributeName === attrName;
        }

        get attributeName() {
            throw new Error('abstract property must be implemented');
        }
        attributeChanged(element, newValue) {
            throw new Error('abstract method must be implemented');
        }
        attributeRemoved(element, attrName) {
            throw new Error('abstract method must be implemented');
        }
    }

    class DxDataStyleAttribute extends DxCustomAttribute {
        #dynamicCssVariableElements = new WeakSet();

        get attributeName() {
            return 'data-dx-style';
        }
        attributeChanged(element, newValue) {
            if(element.tagName === 'LINK')
                this.#applyThemeDynamicStyle(newValue);
            else
                this.#applyElementStyle(element, newValue);
        }
        allowProcessAttributeRemoved(element, attrName) {
            if(super.allowProcessAttributeRemoved(element, attrName))
                return true;
            if(attrName === 'style' && element.hasAttribute(this.attributeName))
                return true;
            return false;
        }
        attributeRemoved(element, attrName) {
            if(element.tagName === 'LINK' && attrName === this.attributeName)
                this.#applyThemeDynamicStyle('');
            else
                this.#resetStyle(element);

            if(attrName === 'style' && element.hasAttribute(this.attributeName)) { // Blazor 'synchronizeAttributes' remove applied style
                queueMicrotask(() => {
                    this.#applyElementStyle(element, element.getAttribute(this.attributeName));
                });
            }
        }

        #applyElementStyle(element, styleValue) {
            if(element.appliedDxStyle === undefined || element.appliedDxStyle !== styleValue) {
                this.#applyElementStyleCore(element, styleValue);
                element.appliedDxStyle = styleValue;
            }
        }
        #applyElementStyleCore(element, styleValue) {
            if(this.#dynamicCssVariableElements.has(element) || styleValue && styleValue.startsWith('{')) {
                this.#dynamicCssVariableElements.add(element);

                for(let i = element.style.length - 1; i >= 0; i--) {
                    const propertyName = element.style.item(i);
                    if(propertyName.startsWith('--dxbl-dcv-'))
                        element.style.removeProperty(propertyName);
                }

                if(styleValue) {
                    const dynamicCssVariables = JSON.parse(styleValue);
                    for(let name in dynamicCssVariables) {
                        if(name.startsWith('--dxbl-'))
                            element.style.setProperty(name, dynamicCssVariables[name]);
                    }
                }
            } else {
                element.style.cssText = styleValue;
            }
        }
        #resetStyle(element) {
            element.style.cssText = '';
            delete element.appliedDxStyle;
        }
        #applyThemeDynamicStyle(styleValue) {
            this.#getThemeDynamicStyleSheet().replaceSync(styleValue);
        }
        #getThemeDynamicStyleSheet() {
            if(!this.styleSheet) {
                this.styleSheet = new CSSStyleSheet();
                document.adoptedStyleSheets.push(this.styleSheet);
            }
            return this.styleSheet;
        }
    }
    DxCustomAttribute.register(new DxDataStyleAttribute());

    window['DxAttributeWatcher'] = (function () {
        const elementAddedObserver = new MutationObserver(mutations => {
            const nodesToAnalyze = new Set();
            mutations.forEach(entry => {
                entry.addedNodes.forEach(node => node.nodeType === 1 && nodesToAnalyze.add(node));
            });
            processNodes(nodesToAnalyze);
        });
        function wrapNativeMethods() {
            const originalAppedChild = Node.prototype.appendChild;
            const originalInsertBefore = Node.prototype.insertBefore;
            const originalSetAttribute = HTMLElement.prototype.setAttribute;
            const originalRemoveAttribute = HTMLElement.prototype.removeAttribute;

            Node.prototype.appendChild = function() {
                processNodes([arguments[0]]);
                return originalAppedChild.apply(this, arguments);
            };
            Node.prototype.insertBefore = function() {
                processNodes([arguments[0]]);
                return originalInsertBefore.apply(this, arguments);
            };
            HTMLElement.prototype.setAttribute = function() {
                attributeChanged(this, arguments[0], arguments[1]);
                return originalSetAttribute.apply(this, arguments);
            };
            HTMLElement.prototype.removeAttribute = function() {
                attributeRemoved(this, arguments[0]);
                return originalRemoveAttribute.apply(this, arguments);
            };
        }

        function processNodes(nodesToAnalyze) {
            DxCustomAttribute.attributes.forEach(customAttribute => {
                const nodesToProcess = new Set();

                nodesToAnalyze.forEach(root => {
                    if(root.hasAttribute && root.hasAttribute(customAttribute.attributeName))
                        nodesToProcess.add(root);
                    if(root.childElementCount && root.childElementCount > 0 && root.querySelectorAll)
                        root.querySelectorAll(`[${customAttribute.attributeName}]`).forEach(node => nodesToProcess.add(node));
                });

                nodesToProcess.forEach(node => {
                    customAttribute.attributeChanged(node, node.getAttribute(customAttribute.attributeName))
                });
            });
        }
        function attributeChanged(element, attrName, value) {
            DxCustomAttribute.attributes.forEach(customAttribute => {
                if(attrName === customAttribute.attributeName)
                    customAttribute.attributeChanged(element, value);
            });
        }
        function attributeRemoved(element, attrName) {
            DxCustomAttribute.attributes.forEach(customAttribute => {
                if(customAttribute.allowProcessAttributeRemoved(element, attrName))
                    customAttribute.attributeRemoved(element, attrName);
            });
        }

        return {
            start() {
                elementAddedObserver.observe(document.documentElement, { subtree: true, childList: true });
                wrapNativeMethods();

                processNodes([document.documentElement]);
            }
        };
    })();

    window['DxAttributeWatcher'].start();
}
