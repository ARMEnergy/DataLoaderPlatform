# Energy Aspects loader

## API
- Reference: https://developer.energyaspects.com/reference/overview
- Auth: API key. Key is stored in config as ENERGY_ASPECTS_API_KEY
  (do NOT hard-code it). Never place the key in this file.

## Discovery step (run first)
- Dataset mappings: https://developer.energyaspects.com/reference/dataset_mappings_get
- Returns a list of mappings; each has a name and a list of dataset IDs.
- Each record has a `request_string` showing an example GET query.
- Use the dataset IDs to call the individual dataset endpoints.

## Datasets required (current scope)
- Canada oil production
- Canada balances
- Mexican production
- China crude data
- India demand, import, crude, production, runs
- US crude balances
- Cushing crude microbalance
- US crude production & upstream activity
- Canada gas balances
- North America basis price forecasts
- Monthly product inventories
- Europe diesel balances
- Europe gasoline balances
- Europe jet balances
- US gasoline balances
- US diesel balances
- US weekly product stock forecast
- China oil products data
- Middle distillate price forecasts
- Fuel oil price forecasts
- Light ends price forecasts
- Dispatch costs by ISO
- US installed capacity by ISO and fuel type
- Monthly load by ISO