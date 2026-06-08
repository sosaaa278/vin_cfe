# TODO - Inconformidades Meta (grafica)

- [ ] Revisar InconformidadesMetaComponent para problema de render del canvas Chart.js
- [ ] Corregir timing: render chart sólo cuando el canvas esté listo (usar ngAfterViewChecked + bandera)
- [ ] Eliminar/evitar setTimeout innecesario dentro de processChartData
- [ ] Asegurar que renderChart destruya la instancia previa y no intente render antes de tiempo
- [ ] Probar: click en “Generar Dashboard” debe mostrar la gráfica y graficar datos del endpoint /scrape

